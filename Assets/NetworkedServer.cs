using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.UI;

public class NetworkedServer : MonoBehaviour
{
    const int maxConnections = 1000;
    int reliableChannelID;
    int unreliableChannelID;
    int hostID;
    const int socketPort = 5491;

    string playerAccountsFilepath;

    int playerWaitingForMatchWithID = -1;

    LinkedList<PlayerAccount> playerAccounts;
    LinkedList<GameRoom> gameRooms;

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
        gameRooms = new LinkedList<GameRoom>();
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
                SendMessageToClient(ServerToClientSignifiers.AccountCreationFailed + "", id);
            }
            else
            {
                PlayerAccount newPlayerAccount = new PlayerAccount(n, p);
                playerAccounts.AddLast(newPlayerAccount);
                SendMessageToClient(ServerToClientSignifiers.AccountCreationComplete + "", id);
                // Save to list HD
                SavePlayerAccounts();
            }
        }
        else if (signifier == ClientToServerSignifiers.Login)
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
        else if (signifier == ClientToServerSignifiers.JoinQueueForGameRoom)
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

                SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.O, playerWaitingForMatchWithID);
                SendMessageToClient(ServerToClientSignifiers.GameStart + "," + TeamSignifier.X, id);

                playerWaitingForMatchWithID = -1;
            }

        }
        else if (signifier == ClientToServerSignifiers.TestPlay)
        {
            GameRoom gr = GetGameRoomWithClientID(id);

            if (gr != null)
            {
                var location = int.Parse(csv[1]);

                if (gr.playerID1 == id)
                {
                    gr.gameBoard[location] = TeamSignifier.O;
                    gr.replayInfo += location + "." + TeamSignifier.O;

                    // Check for a win
                    if (gr.CheckWin())
                    {
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.O + "," + WinStates.Loss, gr.playerID2);

                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Win, gr.playerID1);
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Loss, gr.playerID2);

                    }
                    else if (gr.CheckTie())
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Tie, gr.playerID1);
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Tie, gr.playerID2);
                    }
                    else
                    {
                        gr.replayInfo += ";";
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.O + "," + WinStates.ContinuePlay, gr.playerID2);
                    }
                }
                else
                {
                    gr.gameBoard[location] = TeamSignifier.X;
                    gr.replayInfo += location + "." + TeamSignifier.X;

                    if (gr.CheckWin())
                    {
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.X + "," + WinStates.Loss, gr.playerID1);

                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Loss, gr.playerID1);
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Win, gr.playerID2);

                    }
                    else if (gr.CheckTie())
                    {
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Tie, gr.playerID1);
                        SendMessageToClient(ServerToClientSignifiers.GameOver + "," + WinStates.Tie, gr.playerID2);
                    }
                    else
                    {
                        gr.replayInfo += ";";
                        SendMessageToClient(ServerToClientSignifiers.OpponentPlayed + "," + location + "," + TeamSignifier.X + "," + WinStates.ContinuePlay, gr.playerID1);
                    }

                }
            }
        }

        else if (signifier == ClientToServerSignifiers.LeaveRoom)
        {
            // Remove ID from game rooms
            GameRoom gr = GetGameRoomWithClientID(id);
            gr.RemoveMatchingID(id);
        }
        else if (signifier == ClientToServerSignifiers.TextMessage)
        {
            var message = "Player " + id + ": " + csv[1];

            GameRoom gr = GetGameRoomWithClientID(id);

            SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, gr.playerID1);
            SendMessageToClient(ServerToClientSignifiers.TextMessage + "," + message, gr.playerID2);
        }
        else if (signifier == ClientToServerSignifiers.RequestReplay)
        {
            GameRoom gr = GetGameRoomWithClientID(id);

            SendMessageToClient(ServerToClientSignifiers.ReplayInformation + "," + gr.replayInfo, id);
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
        foreach (GameRoom gr in gameRooms)
        {
            if (gr.playerID1 == id || gr.playerID2 == id)
            {
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
            gr = new GameRoom(ID1, ID2);
            gameRooms.AddLast(gr);
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
    public int playerID1, playerID2;

    public int[] gameBoard = new int[9];

    public string replayInfo;

    public GameRoom(int PlayerID1, int PlayerID2)
    {
        playerID1 = PlayerID1;
        playerID2 = PlayerID2;

        ResetBoard();
    }

    public void SetupRoom(int PlayerID1, int PlayerID2)
    {
        if (CheckAvailable())
        {

            playerID1 = PlayerID1;
            playerID2 = PlayerID2;

            ResetBoard();
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
        if (CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.TopMid], gameBoard[Board.TopRight]) ||
            CompareSlots(gameBoard[Board.MidLeft], gameBoard[Board.MidMid], gameBoard[Board.MidRight]) ||
            CompareSlots(gameBoard[Board.BotLeft], gameBoard[Board.BotMid], gameBoard[Board.BotRight]) ||
            CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.MidLeft], gameBoard[Board.BotLeft]) ||
            CompareSlots(gameBoard[Board.TopMid], gameBoard[Board.MidMid], gameBoard[Board.BotMid]) ||
            CompareSlots(gameBoard[Board.TopRight], gameBoard[Board.MidRight], gameBoard[Board.BotRight]) ||
            CompareSlots(gameBoard[Board.TopLeft], gameBoard[Board.MidMid], gameBoard[Board.BotRight]) ||
            CompareSlots(gameBoard[Board.TopRight], gameBoard[Board.MidMid], gameBoard[Board.BotLeft]))
            return true;

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

            return true;
        }

        return false;
    }

    public void RemoveMatchingID(int id)
    {
        // Remove matching ID
        if (playerID1 == id)
        {
            playerID1 = -1;
        }
        else if (playerID2 == id)
        {
            playerID2 = -1;
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
    public const int TestPlay = 4;
    public const int LeaveRoom = 5;
    public const int TextMessage = 6;
    public const int RequestReplay = 7;
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
}

public static class WinStates
{
    public const int ContinuePlay = 0;
    public const int Win = 1;
    public const int Loss = 2;
    public const int Tie = 3;
}