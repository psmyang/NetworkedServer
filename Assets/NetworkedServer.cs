using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.UI;

public class NetworkedServer : MonoBehaviour
{
    int maxConnections = 1000;
    int reliableChannelID;
    int unreliableChannelID;
    int hostID;
    int socketPort = 5491;

    string playerAccountsFilepath;

    int playerWaitingForMatchWithID = -1;

    LinkedList<PlayerAccount> playerAccounts;
    List<GameRoom> gameRooms;

    // Start is called before the first frame update
    void Start()
    {
        NetworkTransport.Init();
        ConnectionConfig config = new ConnectionConfig();
        reliableChannelID = config.AddChannel(QosType.Reliable);
        unreliableChannelID = config.AddChannel(QosType.Unreliable);
        HostTopology topology = new HostTopology(config, maxConnections);
        hostID = NetworkTransport.AddHost(topology, socketPort, null);

        playerAccounts = new LinkedList<PlayerAccount>();
        gameRooms = new List<GameRoom>();
        playerAccountsFilepath = Application.dataPath + Path.DirectorySeparatorChar + "Accounts.txt";

        // Read in player accounts
        LoadPlayerAccounts();
    }

    // Update is called once per frame
    void Update()
    {

        int recHostID;
        int recConnectionID;
        int recChannelID;
        byte[] recBuffer = new byte[1024];
        int bufferSize = 1024;
        int dataSize;
        byte error = 0;

        NetworkEventType recNetworkEvent = NetworkTransport.Receive(out recHostID, out recConnectionID, out recChannelID, recBuffer, bufferSize, out dataSize, out error);

        switch (recNetworkEvent)
        {
            case NetworkEventType.Nothing:
                break;
            case NetworkEventType.ConnectEvent:
                Debug.Log("Connection, " + recConnectionID);
                break;
            case NetworkEventType.DataEvent:
                string msg = Encoding.Unicode.GetString(recBuffer, 0, dataSize);
                ProcessRecievedMsg(msg, recConnectionID);
                break;
            case NetworkEventType.DisconnectEvent:
                Debug.Log("Disconnection, " + recConnectionID);

                // Remove Player from game room, if they were in one
                ProcessRecievedMsg(ClientToServerSignifiers.LeaveRoom + "", recConnectionID);
                break;
        }

    }
  
    public void SendMessageToClient(string msg, int id)
    {
        byte error = 0;
        byte[] buffer = Encoding.Unicode.GetBytes(msg);
        NetworkTransport.Send(hostID, id, reliableChannelID, buffer, msg.Length * sizeof(char), out error);
    }
    
    private void ProcessRecievedMsg(string msg, int id)
    {
        Debug.Log("msg recieved = " + msg + ".  connection id = " + id);

        string[] csv = msg.Split(',');

        int signifier = int.Parse(csv[0]);

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            // Check if player account name already exists, 
            string n = csv[1];
            string p = csv[2];
            bool nameInUse = false;

            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == n)
                {
                    nameInUse = true;
                    break;
                }
            }
            
            if (nameInUse)
            {
                SendMessageToClient(ServerToClientSignifiers.AccountCreationFailed + ",: Name already in use", id); 
            }
            else
            {
                PlayerAccount newPlayerAccount = new PlayerAccount(n, p);
                playerAccounts.AddLast(newPlayerAccount);
                SendMessageToClient(ServerToClientSignifiers.AccountCreationComplete + ",: Succesful Account Creation", id);

                // Save to list HD
                SavePlayerAccounts();
            }
        }
        else

        if (signifier == ClientToServerSignifiers.Login)
        {
            // Check if player account name already exists, 
            PlayerAccount loginPlayer = null;
            string n = csv[1];
            string p = csv[2];
            bool nameInUse = false;

            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == n)
                {
                    loginPlayer = pa;
                    nameInUse = true;
                    break;
                }
            }

            if (nameInUse)
            {
                // Check password, if correct
                if (p == loginPlayer.password)
                {
                    SendMessageToClient(ServerToClientSignifiers.LoginComplete + ",: Successful Login", id);
                }
                else
                {
                    // Password is not correct
                    SendMessageToClient(ServerToClientSignifiers.LoginFailed + ",: Wrong Password", id);
                }

            }
            else
            {
                // Login does not exist
                SendMessageToClient(ServerToClientSignifiers.LoginFailed + ",: No Account exists", id);
            }
        }
        else 

        if (signifier == ClientToServerSignifiers.JoinQueueForGameRoom)
        {
            Debug.Log("Get the Player into a waiting queue!");

            if (playerWaitingForMatchWithID == -1)
            {
                playerWaitingForMatchWithID = id;
            }
            else
            {
                GameRoom gr = GetAvailableGameRoom(playerWaitingForMatchWithID, id);
                gr.ResetBoard();

                // 0 plays first, 1 plays second
                SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.O, playerWaitingForMatchWithID);
                SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.X, id);

                // Send information to observers as well
                foreach (var observer in gr.observerIDs)
                {
                    SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.None, observer);
                }

                playerWaitingForMatchWithID = -1;
            }

        }
        else

        if (signifier == ClientToServerSignifiers.TicTacToePlay)
        {
            // Get game room for client ID
            GameRoom gr = GetGameRoomWithClientID(id);

            // If game room exists
            if (gr != null)
            {
                var location = int.Parse(csv[1]);

                if (gr.playerID1 == id)
                {

                    gr.gameBoard[location] = TeamSignifier.O;
                    gr.replayInfo += location + "." + TeamSignifier.O;

                    if (gr.CheckWin())
                    {
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.O + "," + WinStates.OsWin, gr.playerID2);

                        foreach (var observer in gr.observerIDs)
                        {
                            SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.O + "," + WinStates.OsWin, observer);
                        }

                        DeclareResult(gr, WinStates.OsWin);
                    }
                    else if (gr.CheckTie())
                    {
                        DeclareResult(gr, WinStates.Tie);
                    }
                    else
                    {
                        gr.replayInfo += ";";
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.O + "," + WinStates.ContinuePlay, gr.playerID2);

                        foreach (var observer in gr.observerIDs)
                        {
                            SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.O + "," + WinStates.ContinuePlay, observer);
                        }
                    }

                }
                else
                {
                    gr.gameBoard[location] = TeamSignifier.X;
                    gr.replayInfo += location + "." + TeamSignifier.X;

                    if (gr.CheckWin())
                    {
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.X + "," + WinStates.XsWin, gr.playerID1);

                        foreach (var observer in gr.observerIDs)
                        {
                            SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.X + "," + WinStates.XsWin, observer);
                        }

                        DeclareResult(gr, WinStates.XsWin);
                    }
                    else if (gr.CheckTie())
                    {
                        DeclareResult(gr, WinStates.Tie);
                    }
                    else
                    {
                        gr.replayInfo += ";";
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.X + "," + WinStates.ContinuePlay, gr.playerID1);

                        foreach (var observer in gr.observerIDs)
                        {
                            SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.X + "," + WinStates.ContinuePlay, observer);
                        }
                    }

                }
            }
        }
        else

        if (signifier == ClientToServerSignifiers.LeaveRoom)
        {
            GameRoom gr = GetGameRoomWithClientID(id);

            if (gr != null)
            {
                bool wasObserver = false;

                foreach (var observer in gr.observerIDs)
                {
                    if (observer == id)
                    {
                        wasObserver = true;
                        break;
                    }
                }

                if (!wasObserver && gr.gameInProgress)
                {
                    Debug.Log("Game was in progress... Awarding a win");


                    int winner = WinStates.Tie;

                    if (gr.playerID1 == id)
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.XsWin, gr.playerID2);
                        winner = WinStates.XsWin;
                    }
                    else if (gr.playerID2 == id)
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.OsWin, gr.playerID1);
                        winner = WinStates.OsWin;
                    }

                    foreach (var observer in gr.observerIDs)
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winner, observer);
                    }

                    gr.gameInProgress = false;

                    if (gr.replayInfo.Length > 0)
                        gr.replayInfo = gr.replayInfo.Substring(0, gr.replayInfo.Length - 1);
                }

                Debug.Log("Removing Player from Game Room");

                gr.RemoveMatchingID(id);
            }
        }
        else 

        if (signifier == ClientToServerSignifiers.TextMessage)
        {
            var message = "Player " + id + ": " + csv[1];

            GameRoom gr = GetGameRoomWithClientID(id);

            if (gr != null)
            {
                SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, gr.playerID1);
                SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, gr.playerID2);

                foreach (var observer in gr.observerIDs)
                {
                    SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, observer);
                }
            }
            
        }
        else

        if (signifier == ClientToServerSignifiers.RequestReplay)
        {
            GameRoom gr = GetGameRoomWithClientID(id);

            SendMessageToClient(ServerToClientSignifiers.ReplayInformation + "," + gr.replayInfo, id);
        }
        else

        if (signifier == ClientToServerSignifiers.GetServerList)
        {
            foreach (var room in gameRooms)
            {
                int roomID = room.roomID;
                SendMessageToClient(ServerToClientSignifiers.ServerList + "," + roomID + "," + room.observerIDs.Count, id);
            }
        }
        else

        if (signifier == ClientToServerSignifiers.SpectateGame)
        {
            int roomID = int.Parse(csv[1]);

            gameRooms[roomID].observerIDs.Add(id);
            SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.None, id);
        }
    }

    private void DeclareResult(GameRoom gr, int winState)
    {
        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winState, gr.playerID1);
        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winState, gr.playerID2);

        foreach (var observer in gr.observerIDs)
        {
            SendMessageToClient(ServerToClientSignifiers.GameOver + "," + winState, observer);
        }
    }

    private void SavePlayerAccounts()
    {
        StreamWriter sw = new StreamWriter(playerAccountsFilepath);

        foreach (PlayerAccount pa in playerAccounts)
        {
            sw.WriteLine(pa.name + "," + pa.password);
        }

        sw.Close();
    }

    private void LoadPlayerAccounts()
    {
        if (File.Exists(playerAccountsFilepath))
        {
            StreamReader sr = new StreamReader(playerAccountsFilepath);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                Debug.Log(line);

                string[] csv = line.Split(',');

                PlayerAccount pa = new PlayerAccount(csv[0], csv[1]);

                playerAccounts.AddLast(pa);
            }

            sr.Close();
        }
    }

    private GameRoom GetGameRoomWithClientID(int id)
    {
        foreach(GameRoom gr in gameRooms)
        {
            if (gr.playerID1 == id || gr.playerID2 == id)
            {
                return gr;
            }

            foreach (var observer in gr.observerIDs)
            {
                if (observer == id)
                    return gr;
            }
        }

        return null;
    }

    private GameRoom GetAvailableGameRoom(int ID1, int ID2)
    {
        GameRoom gr = null;

        foreach (var room in gameRooms)
        {
            if (room != null && room.CheckAvailable())
            {
                gr = room;
                break;
            }
        }

        if (gr == null)
        {
            gr = new GameRoom(gameRooms.Count, ID1, ID2);
            gameRooms.Add(gr);
        }

        gr.SetupRoom(ID1, ID2);
        return gr;
    }
}


public class PlayerAccount
{
    public string name, password;

    public PlayerAccount(string Name, string Password)
    {
        name = Name;
        password = Password;
    }
}

public class GameRoom
{
    public int roomID;

    public int playerID1, playerID2;

    public List<int> observerIDs = new List<int>();

    public int[] gameBoard = new int[9];

    public string replayInfo;

    public bool gameInProgress = false;

    public GameRoom(int index, int PlayerID1, int PlayerID2)
    {
        roomID = index;
        playerID1 = PlayerID1;
        playerID2 = PlayerID2;

        ResetBoard();
        gameInProgress = true;
    }

    public void SetupRoom(int PlayerID1, int PlayerID2)
    {
        if (CheckAvailable())
        {

            playerID1 = PlayerID1;
            playerID2 = PlayerID2;

            ResetBoard();
            gameInProgress = true;
        }
    }

    public bool CompareSlots(int slot1, int slot2, int slot3)
    {
        if (slot1 != TeamSignifier.None)
        {
            if (slot1 == slot2 && slot2 == slot3)
            {
                return true;
            }
        }

        return false;
    }

    public bool CheckWin()
    {
        if (CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.TopMid], gameBoard[Board.TopRight])       ||
            CompareSlots(gameBoard[Board.MidLeft], gameBoard[Board.MidMid], gameBoard[Board.MidRight])       ||
            CompareSlots(gameBoard[Board.BotLeft], gameBoard[Board.BotMid], gameBoard[Board.BotRight])       ||
            CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.MidLeft], gameBoard[Board.BotLeft])       ||
            CompareSlots(gameBoard[Board.TopMid], gameBoard[Board.MidMid], gameBoard[Board.BotMid])          ||
            CompareSlots(gameBoard[Board.TopRight], gameBoard[Board.MidRight], gameBoard[Board.BotRight])    ||
            CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.MidMid], gameBoard[Board.BotRight])       ||
            CompareSlots(gameBoard[Board.TopRight], gameBoard[Board.MidMid], gameBoard[Board.BotLeft]))
        {
            gameInProgress = false;
            return true;
        }

        return false;
    }

    public bool CheckTie()
    {
        if (!CheckWin())
        {
            foreach (var slot in gameBoard)
            {
                if (slot == TeamSignifier.None)
                    return false;
            }

            gameInProgress = false;
            return true;
        }

        return false;
    }

    public void RemoveMatchingID(int id)
    {
        if (playerID1 == id)
        {
            playerID1 = -1;
        }
        else if (playerID2 == id)
        {
            playerID2 = -1;
        }

        foreach (var observer in observerIDs)
        {
            if (observer == id)
            {
                observerIDs.Remove(id);
                break;
            }
        }
    }

    public bool CheckAvailable()
    {
        if (playerID1 == -1 && playerID2 == -1)
        {
            return true;
        }

        return false;
    }

    public void ResetBoard()
    {
        replayInfo = "";

        for (int i = 0; i < gameBoard.Length; i++)
        {
            gameBoard[i] = TeamSignifier.None;
        }
    }
}

public static class Board
{
    public const int TopLeft = 0;
    public const int TopMid = 1;
    public const int TopRight = 2;
    public const int MidLeft = 3;
    public const int MidMid = 4;
    public const int MidRight = 5;
    public const int BotLeft = 6;
    public const int BotMid = 7;
    public const int BotRight = 8;
}

public static class TeamSignifier
{
    public const int None = -1;
    public const int O = 0;
    public const int X = 1;
}

public static class ClientToServerSignifiers
{
    public const int CreateAccount = 1;
    public const int Login = 2;

    public const int JoinQueueForGameRoom = 3;

    public const int TicTacToePlay = 4;

    public const int LeaveRoom = 5;

    public const int TextMessage = 6;

    public const int RequestReplay = 7;
    public const int GetServerList = 8;
    public const int SpectateGame = 9;
}

public static class ServerToClientSignifiers
{
    public const int LoginComplete = 1;
    public const int LoginFailed = 2;
    public const int AccountCreationComplete = 3;
    public const int AccountCreationFailed = 4;

    public const int OpponentPlayed = 5;
    public const int GameStart = 6;

    public const int GameOver = 7;

    public const int TextMessage = 8;

    public const int ReplayInformation = 9;

    public const int ServerList = 10;
}

public static class WinStates
{
    public const int ContinuePlay = 0;
    public const int OsWin = 1;
    public const int XsWin = 2;
    public const int Tie = 3;
}