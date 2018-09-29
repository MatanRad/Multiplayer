﻿using Harmony;
using Ionic.Zlib;
using Multiplayer.Common;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace Multiplayer.Client
{
    public class ClientJoiningState : MpConnectionState
    {
        public ClientJoiningState(IConnection connection) : base(connection)
        {
            connection.Send(Packets.CLIENT_USERNAME, Multiplayer.username);
            connection.Send(Packets.CLIENT_REQUEST_WORLD);
        }

        [PacketHandler(Packets.SERVER_WORLD_DATA)]
        public void HandleWorldData(ByteReader data)
        {
            connection.State = ConnectionStateEnum.ClientPlaying;
            Log.Message("Game data size: " + data.GetBytes().Length);

            int factionId = data.ReadInt32();
            Multiplayer.session.myFactionId = factionId;

            int tickUntil = data.ReadInt32();

            int globalCmdsLen = data.ReadInt32();
            List<ScheduledCommand> globalCmds = new List<ScheduledCommand>(globalCmdsLen);
            for (int i = 0; i < globalCmdsLen; i++)
                globalCmds.Add(ScheduledCommand.Deserialize(new ByteReader(data.ReadPrefixedBytes())));

            OnMainThread.cachedMapCmds[ScheduledCommand.Global] = globalCmds;

            byte[] worldData = GZipStream.UncompressBuffer(data.ReadPrefixedBytes());
            OnMainThread.cachedGameData = worldData;

            List<int> mapIds = new List<int>();
            int maps = data.ReadInt32();
            for (int i = 0; i < maps; i++)
            {
                int mapId = data.ReadInt32();

                int mapCmdsLen = data.ReadInt32();
                List<ScheduledCommand> mapCmds = new List<ScheduledCommand>(mapCmdsLen);
                for (int j = 0; j < mapCmdsLen; j++)
                    mapCmds.Add(ScheduledCommand.Deserialize(new ByteReader(data.ReadPrefixedBytes())));

                OnMainThread.cachedMapCmds[mapId] = mapCmds;

                byte[] mapData = GZipStream.UncompressBuffer(data.ReadPrefixedBytes());
                OnMainThread.cachedMapData[mapId] = mapData;
                mapIds.Add(mapId);
            }

            ReloadGame(tickUntil, mapIds, () =>
            {
                Multiplayer.Client.Send(Packets.CLIENT_WORLD_LOADED);
            });
        }

        public static void ReloadGame(int tickUntil, List<int> maps, Action onDone = null)
        {
            XmlDocument gameDoc = ScribeUtil.GetDocument(OnMainThread.cachedGameData);
            XmlNode gameNode = gameDoc.DocumentElement["game"];

            foreach (int map in maps)
            {
                using (XmlReader reader = XmlReader.Create(new MemoryStream(OnMainThread.cachedMapData[map])))
                {
                    XmlNode mapNode = gameDoc.ReadNode(reader);
                    gameNode["maps"].AppendChild(mapNode);

                    if (gameNode[Multiplayer.CurrentMapIndexXmlKey] == null)
                        gameNode.AddNode(Multiplayer.CurrentMapIndexXmlKey, map.ToString());
                }
            }

            TickPatch.tickUntil = tickUntil;
            LoadPatch.gameToLoad = gameDoc;

            LongEventHandler.QueueLongEvent(() =>
            {
                MemoryUtility.ClearAllMapsAndWorld();
                Current.Game = new Game();
                Current.Game.InitData = new GameInitData();
                Current.Game.InitData.gameToLoad = "server";

                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    LongEventHandler.QueueLongEvent(CatchUp(onDone), "MpSimulating", null);
                });
            }, "Play", "MpLoading", true, null);
        }

        public static IEnumerable CatchUp(Action finishAction)
        {
            FactionWorldData factionData = Multiplayer.WorldComp.factionData.GetValueSafe(Multiplayer.session.myFactionId);
            if (factionData != null && factionData.online)
                Multiplayer.RealPlayerFaction = Find.FactionManager.GetById(factionData.factionId);
            else
                Multiplayer.RealPlayerFaction = Multiplayer.DummyFaction;

            Multiplayer.WorldComp.cmds = new Queue<ScheduledCommand>(OnMainThread.cachedMapCmds.GetValueSafe(ScheduledCommand.Global) ?? new List<ScheduledCommand>());
            foreach (Map m in Find.Maps)
                m.AsyncTime().cmds = new Queue<ScheduledCommand>(OnMainThread.cachedMapCmds.GetValueSafe(m.uniqueID) ?? new List<ScheduledCommand>());

            int start = TickPatch.Timer;
            int startTicks = Find.Maps[0].AsyncTime().mapTicks;
            float startTime = Time.realtimeSinceStartup;

            while (TickPatch.Timer < TickPatch.tickUntil)
            {
                TickPatch.accumulator = Math.Min(60, TickPatch.tickUntil - TickPatch.Timer);

                SimpleProfiler.Start();
                Multiplayer.simulating = true;
                TickPatch.Tick();
                Multiplayer.simulating = false;
                SimpleProfiler.Pause();

                int pct = (int)((float)(TickPatch.Timer - start) / (TickPatch.tickUntil - start) * 100);
                float tps = (Find.Maps[0].AsyncTime().mapTicks - startTicks) / (Time.realtimeSinceStartup - startTime);
                LongEventHandler.SetCurrentEventText($"Loading game {pct}/100 " + TickPatch.Timer + " " + TickPatch.tickUntil + " " + Find.Maps[0].AsyncTime().mapTicks + " " + tps);

                yield return null;
            }

            foreach (Map map in Find.Maps)
            {
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                    pawn.drawer.tweener.tweenedPos = pawn.drawer.tweener.TweenedPosRoot();
            }

            SimpleProfiler.Print("prof_sim.txt");
            SimpleProfiler.Init("");

            finishAction?.Invoke();
        }

        [PacketHandler(Packets.SERVER_DISCONNECT_REASON)]
        public void HandleDisconnectReason(ByteReader data)
        {
            string reason = data.ReadString();
            Multiplayer.session.disconnectServerReason = reason;
        }
    }

    public class ClientPlayingState : MpConnectionState, IConnectionStatusListener
    {
        public ClientPlayingState(IConnection connection) : base(connection)
        {
        }

        [PacketHandler(Packets.SERVER_TIME_CONTROL)]
        public void HandleTimeControl(ByteReader data)
        {
            int tickUntil = data.ReadInt32();
            TickPatch.tickUntil = tickUntil;
        }

        [PacketHandler(Packets.SERVER_KEEP_ALIVE)]
        public void HandleKeepAlive(ByteReader data)
        {
            int id = data.ReadInt32();
            connection.Send(Packets.CLIENT_KEEP_ALIVE, id);
        }

        [PacketHandler(Packets.SERVER_COMMAND)]
        public void HandleCommand(ByteReader data)
        {
            ScheduledCommand cmd = ScheduledCommand.Deserialize(data);
            cmd.issuedBySelf = data.ReadBool();
            OnMainThread.ScheduleCommand(cmd);
        }

        [PacketHandler(Packets.SERVER_PLAYER_LIST)]
        public void HandlePlayerList(ByteReader data)
        {
            string[] playerList = data.ReadPrefixedStrings();
            Multiplayer.Chat.playerList = playerList;
        }

        [PacketHandler(Packets.SERVER_CHAT)]
        public void HandleChat(ByteReader data)
        {
            string username = data.ReadString();
            string msg = data.ReadString();

            Multiplayer.Chat.AddMsg(username + ": " + msg);
        }

        [PacketHandler(Packets.SERVER_MAP_RESPONSE)]
        public void HandleMapResponse(ByteReader data)
        {
            int mapId = data.ReadInt32();

            int mapCmdsLen = data.ReadInt32();
            List<ScheduledCommand> mapCmds = new List<ScheduledCommand>(mapCmdsLen);
            for (int j = 0; j < mapCmdsLen; j++)
                mapCmds.Add(ScheduledCommand.Deserialize(new ByteReader(data.ReadPrefixedBytes())));

            OnMainThread.cachedMapCmds[mapId] = mapCmds;

            byte[] mapData = GZipStream.UncompressBuffer(data.ReadPrefixedBytes());
            OnMainThread.cachedMapData[mapId] = mapData;

            ClientJoiningState.ReloadGame(TickPatch.tickUntil, Find.Maps.Select(m => m.uniqueID).Concat(mapId).ToList());
            // todo Multiplayer.client.Send(Packets.CLIENT_MAP_LOADED);
        }

        [PacketHandler(Packets.SERVER_NOTIFICATION)]
        public void HandleNotification(ByteReader data)
        {
            string msg = data.ReadString();
            Messages.Message(msg, MessageTypeDefOf.SilentInput, false);
        }

        [PacketHandler(Packets.SERVER_DISCONNECT_REASON)]
        public void HandleDisconnectReason(ByteReader data)
        {
            string reason = data.ReadString();
            Multiplayer.session.disconnectServerReason = reason;
        }

        public void Connected()
        {
        }

        public void Disconnected()
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.WindowStack.Add(new DisconnectedWindow(Multiplayer.session.disconnectServerReason ?? Multiplayer.session.disconnectNetReason));
        }
    }

    public class ClientSteamState : MpConnectionState
    {
        public ClientSteamState(IConnection connection) : base(connection)
        {
            connection.Send(Packets.CLIENT_STEAM_REQUEST);
        }

        [PacketHandler(Packets.SERVER_STEAM_ACCEPT)]
        public void HandleSteamAccept(ByteReader data)
        {
            connection.State = ConnectionStateEnum.ClientJoining;
        }

        [PacketHandler(Packets.SERVER_DISCONNECT_REASON)]
        public void HandleDisconnectReason(ByteReader data)
        {
            string reason = data.ReadString();
            Multiplayer.session.disconnectServerReason = reason;
        }
    }

    public class DisconnectedWindow : Window
    {
        public override Vector2 InitialSize => new Vector2(300f, 150f);

        private string reason;

        public DisconnectedWindow(string reason)
        {
            this.reason = reason;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            const float ButtonWidth = 140f;
            const float ButtonHeight = 40f;

            Text.Anchor = TextAnchor.MiddleCenter;
            Rect labelRect = inRect;
            labelRect.yMax -= ButtonHeight;
            Widgets.Label(labelRect, reason);
            Text.Anchor = TextAnchor.UpperLeft;

            Rect buttonRect = new Rect((inRect.width - ButtonWidth) / 2f, inRect.height - ButtonHeight - 10f, ButtonWidth, ButtonHeight);
            if (Widgets.ButtonText(buttonRect, "Quit to main menu", true, false, true))
            {
                GenScene.GoToMainMenu();
            }
        }
    }

    public interface IConnectionStatusListener
    {
        void Connected();
        void Disconnected();
    }

    public static class ConnectionStatusListeners
    {
        public static IEnumerable<IConnectionStatusListener> All
        {
            get
            {
                if (Find.WindowStack != null)
                    foreach (Window window in Find.WindowStack.Windows)
                        if (window is IConnectionStatusListener listener)
                            yield return listener;

                if (Multiplayer.Client.StateObj is IConnectionStatusListener state)
                    yield return state;
            }
        }
    }

}