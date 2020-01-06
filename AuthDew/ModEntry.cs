using System;
using System.Security.Cryptography;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace AuthDew
{
    class AuthDewMessage
    {
        public int messageApiMajor { get; set; } = 1;
        public int messageApiMinor { get; set; } = 0;
        public int messageApiPatch { get; set; } = 0;
        public string messageSenderId { get; set; }
        public string messageBody { get; set; }
    }

    public class ModEntry : Mod
    {
        Dictionary<string, Dictionary<string, string>> clientAuthTable; // (serverID, (farmerName, authCode))
        Dictionary<string, string> inviteCodeTable; // (serverID, inviteCode)
        string senderId;
        bool isServer;
        bool ApiCompatibleBool;
        KeyValuePair<long, KeyValuePair<string, KeyValuePair<string, string>>> pendingAuthCreationServer;
        // (serverConnectedPlayerID, (serverID, (farmerName, authCode)))

        public override void Entry(IModHelper helper)
        {
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.Saved += this.OnSaved;
            clientAuthTable = this.Helper.Data.ReadJsonFile<Dictionary<string, Dictionary<string, string>>>("clientAuthTable.json") ?? new Dictionary<string, Dictionary<string, string>>();
            inviteCodeTable = this.Helper.Data.ReadJsonFile<Dictionary<string, string>>("inviteCodeTable.json") ?? new Dictionary<string, string>();

            // check if a senderId already exists, if not create one
            if (this.Helper.Data.ReadJsonFile<string>("senderId.json") == null)
            {
                senderId = createNewRandomString();
                this.Helper.Data.WriteJsonFile<string>("senderId.json", senderId);
            }
            else
            {
                senderId = this.Helper.Data.ReadJsonFile<string>("senderId.json");
            }

            isServer = false; // assumed to be false, set to true later if proven otherwise
            ApiCompatibleBool = true; // assumed to be true, set to false later if proven otherwise
        }

        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {

            AuthDewMessage message = e.ReadAs<AuthDewMessage>();

            // CLIENT: confirm source (host, modname), receive authRequest, inviteCodeRequest, newAuthCode
            // DEBUG/DEVELOPMENT STUFF - TODO: remove when testing done.
            this.Monitor.Log($"received message of type {e.Type} by mod {e.FromModID} from " +
                $"player {e.FromPlayerID}, is the player host? " +
                $"{this.Helper.Multiplayer.GetConnectedPlayer(e.FromPlayerID).IsHost}");
            this.Monitor.Log($"message contents: API {message.messageApiMajor}.{message.messageApiMinor}." +
            	$"{message.messageApiPatch}, " +
            		$"senderID: {message.messageSenderId}, " +
            			$"body: {message.messageBody}");
            
            // if this is run by host, then don't do anything
            if (Context.IsMainPlayer)
            {
                if (!isServer)
                {
                    this.Monitor.Log("WARNING: Player is main player, AuthDew client functionality disabled.");
                    isServer = true;
                }
                return;
            }

            // if message is not from host, also don't do anything (return)
            if (!this.Helper.Multiplayer.GetConnectedPlayer(e.FromPlayerID).IsHost)
            {
                this.Monitor.Log($"received message from non-host player {e.FromPlayerID} - ignoring");
                return;
            }

            // TODO: make this check optional to allow other server software to interact with the API?
            // Or add this as an option to client and server to open it up?
            if (e.FromModID != "thfr.AuthDew-server")
            {
                this.Monitor.Log($"received message from a mod that's not thfr.AuthDew-server: {e.FromModID}");
                return;
            }

            if (!IsApiCompatible(message))
            {
                if (ApiCompatibleBool)
                {
                    this.Monitor.Log($"ERROR: received message with incompatible API version " +
                        $"{message.messageApiMajor}." +
                        $"{message.messageApiMinor}." +
                        $"{message.messageApiPatch}");
                    ApiCompatibleBool = false;
                }

                // send message to sending server indicating API incompatibility
                SendModMessage("", "ApiIncompatible", "thfr.AuthDew-server", e.FromPlayerID);
                return;
            }

            isServer = false;
            ApiCompatibleBool = true;

            // parse message
            switch (e.Type)
            {
                case "authRequest":
                    // check if there's an entry for the server in clientAuthTable
                    // - if not, send some noAuthAvailable code
                    // - if exists, send the corresponding code to the server as authResponse
                    if (clientAuthTable.ContainsKey(message.messageSenderId))
                    {
                        // Check if the farmerName exists in the clientAuthTable's entry for this server
                        if (!clientAuthTable[message.messageSenderId].ContainsKey(Game1.player.Name))
                        {
                            this.Monitor.Log($"ERROR: no entry for farmer {Game1.player.Name} " +
                            	$"to respond to authRequest from server {e.FromPlayerID}");
                            break;
                        }
                        // send the authCode from lookup in the nested dictionary to server
                        SendModMessage(clientAuthTable[message.messageSenderId][Game1.player.Name], "authResponse",
                            "thfr.AuthDew-server", e.FromPlayerID);
                    }
                    else
                    {
                        SendModMessage("", "noAuthAvailable", "thfr.AuthDew-server", e.FromPlayerID);
                    }
                    break;
                case "inviteCodeRequest":
                    // check if there's an entry for the server in inviteCodeTable
                    // - if not, send some noInviteCodeAvailable code
                    // - if exists, send the corresponding code to the server as inviteCodeResponse
                    if (inviteCodeTable.ContainsKey(message.messageSenderId))
                        SendModMessage(inviteCodeTable[message.messageSenderId], "inviteCodeResponse",
                            "thfr.AuthDew-server", e.FromPlayerID);
                    else
                        SendModMessage("", "noInviteCodeAvailable", "thfr.AuthDew-server", e.FromPlayerID);
                    break;
                case "createNewAuth":
                    // take e.ReadAs<MPAuthModMessage>().messageBody and store in clientAuthTable for this server
                    // then send confirmNewAuth to server
                    this.Monitor.Log($"received instruction to create new auth entry for the connected host: " +
                    	$"{message.messageSenderId}, with body: {message.messageBody}");
                    this.Monitor.Log($"adding server {e.FromPlayerID} as pending auth creation");
                    pendingAuthCreationServer =
                        new KeyValuePair<long, KeyValuePair<string, KeyValuePair<string, string>>>
                        (
                        e.FromPlayerID,
                        new KeyValuePair<string, KeyValuePair<string, string>>(message.messageSenderId,
                        new KeyValuePair<string, string>(Game1.player.Name, message.messageBody))
                        );
                    break;
                // TODO: add handler for "ApiIncompatible" message type
                default:
                    this.Monitor.Log($"received message of unknown type from {e.FromPlayerID}");
                    break;
            }
        }

        private bool IsApiCompatible(AuthDewMessage message)
        {
            if (message.messageApiMajor >= 1 &&
                message.messageApiMinor >= 0 &&
                message.messageApiPatch >= 0)
            {
                return true;
            }
            else
                return false;
        }

        private void SendModMessage(string messageText, string messageType, string receiverModID, long receiverID)
        {
            AuthDewMessage message = new AuthDewMessage();
            message.messageSenderId = senderId;
            message.messageBody = messageText;
            this.Helper.Multiplayer.SendMessage<AuthDewMessage>(message, messageType,
                modIDs: new[] { receiverModID },
                playerIDs: new[] { receiverID });
            this.Monitor.Log($"sent message of type {messageType}, senderID: {message.messageSenderId}, " +
            	$"and body |{message.messageBody}| to player {receiverID}");
        }

        // from github.com/funny-snek/anticheat-and-servercode
        private void SendDirectMessage(long playerID, string text)
        {
            Game1.server.sendMessage(playerID, Multiplayer.chatMessage, Game1.player, this.Helper.Content.CurrentLocaleConstant, text);
        }

        string createNewRandomString()
        {
            // from www.dotnetperls.com/rngcryptoserviceprovider
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            byte[] data = new byte[32];
            rng.GetBytes(data); // fill data buffer with random values
            return BitConverter.ToString(data);
        }

        private void OnSaved(object sender, SavedEventArgs e)
        {
            this.Helper.Data.WriteJsonFile<Dictionary<string, Dictionary<string, string>>>("clientAuthTable.json", clientAuthTable);
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsPlayerFree || !e.IsOneSecond || 
                pendingAuthCreationServer.Key == 0L) // The default value of a long is '0L'.
                return;
            this.Monitor.Log($"New clientAuthTable entry: " +
            	$"{pendingAuthCreationServer.Value.Key}, {Game1.player.Name}, " +
            		$"{pendingAuthCreationServer.Value.Value.Value}");
            clientAuthTable[pendingAuthCreationServer.Value.Key] = new Dictionary<string, string>();
            clientAuthTable[pendingAuthCreationServer.Value.Key][Game1.player.Name] =
                pendingAuthCreationServer.Value.Value.Value;

            // send message to server that new auth entry created
            SendModMessage(Game1.player.Name, "confirmNewAuth", "thfr.AuthDew-server", pendingAuthCreationServer.Key);
            // empty out the pendingAuthCreationServer variable by re-initializing
            pendingAuthCreationServer =
                new KeyValuePair<long, KeyValuePair<string, KeyValuePair<string, string>>>();
        }
    }
}