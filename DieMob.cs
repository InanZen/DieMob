using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Data;
using Terraria;
using Hooks;
using TShockAPI;
using TShockAPI.DB;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;

namespace DieMob
{
    public enum RegionType
    {
        Kill = 0,
        Repel = 1,
        Passive = 2
    }
    public class DieMobRegion
    {
        public string RegionName;
        public Region TSRegion = null;
        public RegionType Type = RegionType.Kill;
        public Dictionary<int, int> ReplaceMobs = new Dictionary<int,int>();
        public bool AffectFriendlyNPCs = false;
        public bool AffectStatueSpawns= false;
    }
    [APIVersion(1, 12)]
    public class DieMobMain : TerrariaPlugin
    {
        private static IDbConnection db;
        private static string savepath = Path.Combine(TShock.SavePath, "DieMob/");
        private static bool initialized = false;
        private static List<DieMobRegion> RegionList = new List<DieMobRegion>();
        private static DateTime lastUpdate = DateTime.UtcNow;
        private static Config config;
        private static RegionManager regionManager;
        public override string Name
        {
            get { return "DieMob Regions"; }
        }
        public override string Author
        {
            get { return "by InanZen"; }
        }
        public override string Description
        {
            get { return "Adds monster protection option to regions"; }
        }
        public override Version Version
        {
            get { return new Version("0.21"); }
        }
        public DieMobMain(Main game)
            : base(game)
        {
            Order = 1;
        }
        public override void Initialize()
        {
            if (!Directory.Exists(savepath))
            {
                Directory.CreateDirectory(savepath);
                CreateConfig();
            }
            SetupDb();
            ReadConfig();
            GameHooks.Update += OnUpdate;
            Hooks.GameHooks.PostInitialize += OnPostInit;
            Commands.ChatCommands.Add(new Command("diemob", DieMobCommand, "diemob", "DieMob", "dm"));

        }
        private void OnPostInit()
        {
            regionManager.ReloadAllRegions();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GameHooks.Update -= OnUpdate;
                Hooks.GameHooks.PostInitialize -= OnPostInit;
            }
            base.Dispose(disposing);
        }
        private void SetupDb()
        {          
            string sql = Path.Combine(savepath, "DieMob.sqlite");
            db = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));            
            SqlTableCreator SQLcreator = new SqlTableCreator(db,(IQueryBuilder)new SqliteQueryCreator());
            var table = new SqlTable("DieMobRegions",
             new SqlColumn("Region", MySqlDbType.VarChar) { Primary = true, Unique = true, Length = 30 },
             new SqlColumn("WorldID", MySqlDbType.Int32),
             new SqlColumn("AffectFriendlyNPCs", MySqlDbType.Int32),
             new SqlColumn("AffectStatueSpawns", MySqlDbType.Int32),
             new SqlColumn("ReplaceMobs", MySqlDbType.Text),
             new SqlColumn("Type", MySqlDbType.Int32)
            );
            SQLcreator.EnsureExists(table);            
            regionManager = TShock.Regions;
            //
        }

        class Config
        {
            public int UpdateInterval = 1000;
            public float RepelPowerModifier = 1.0f;
            public int[] MobsWith0ValueButNotStatueSpawn = new int[] { 5, 135, 136, 128, 129, 130, 131, 115, 116 };
        }
        private static void CreateConfig()
        {
            string filepath = Path.Combine(savepath, "config.json");
            try
            {
                using (var stream = new FileStream(filepath, FileMode.Create, FileAccess.Write, FileShare.Write))
                {
                    using (var sr = new StreamWriter(stream))
                    {
                        config = new Config();
                        var configString = JsonConvert.SerializeObject(config, Formatting.Indented);
                        sr.Write(configString);
                    }
                    stream.Close();
                }
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.Message);
                config = new Config();
            }
        }
        private static bool ReadConfig()
        {
            string filepath = Path.Combine(savepath, "config.json");
            try
            {
                if (File.Exists(filepath))
                {
                    using (var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using (var sr = new StreamReader(stream))
                        {
                            var configString = sr.ReadToEnd();
                            config = JsonConvert.DeserializeObject<Config>(configString);
                        }
                        stream.Close();
                    }
                    return true;
                }
                else
                {
                    Log.ConsoleError("DieMob config not found. Creating new one");
                    CreateConfig();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.Message);
            }
            return false;
        }

        private static void OnWorldLoad()
        {
            Console.WriteLine("Loading DieMobRegions...");
            DieMob_Read();
        }
        private void OnUpdate()
        {
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= config.UpdateInterval)
            {
                lastUpdate = DateTime.UtcNow;
                if (!initialized && Main.worldID > 0)
                {
                    initialized = true;
                    OnWorldLoad();
                }
                try
                {
                    for (int r = RegionList.Count - 1; r >= 0; r--)
                    {
                        if (RegionList[r].TSRegion == null)
                        {
                            lock (db)
                            {
                                db.Query("Delete from DieMobRegions where Region = @0 AND WorldID = @1", RegionList[r].TSRegion.Name, Main.worldID);
                            }
                            RegionList.RemoveAt(r);
                            continue;
                        }
                        DieMobRegion Region = RegionList[r];
                        for (int i = 0; i < Main.npc.Length; i++)
                        {
                            if (Main.npc[i].active)
                            {
                                NPC npc = Main.npc[i];
                                if ((npc.friendly && Region.AffectFriendlyNPCs) || (!npc.friendly && (npc.value > 0 || Region.AffectStatueSpawns || config.MobsWith0ValueButNotStatueSpawn.Contains(npc.type))))
                                {
                                    if (Region.TSRegion.InArea((int)(Main.npc[i].position.X / 16), (int)(Main.npc[i].position.Y / 16)))
                                    {
                                        if (Region.ReplaceMobs.ContainsKey(npc.type))
                                        {
                                            npc.SetDefaults(Region.ReplaceMobs[npc.type]);
                                            NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, "", i);
                                        }
                                        else if (Region.Type == RegionType.Repel)
                                        {
                                            Rectangle area = Region.TSRegion.Area;
                                            int yDir = -10;
                                            if (area.Bottom - (int)(npc.position.Y / 16) < area.Height / 2)
                                                yDir = 10;
                                            int xDir = -10;
                                            if (area.Right - (int)(npc.position.X / 16) < area.Width / 2)
                                                xDir = 10;
                                            npc.velocity = new Vector2(xDir * config.RepelPowerModifier, yDir * config.RepelPowerModifier);
                                            NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, "", i);
                                        }
                                        else if (Region.Type == RegionType.Kill)
                                        {
                                            Main.npc[i].netDefaults(0);
                                            TSPlayer.Server.StrikeNPC(i, 99999, 90f, 1);
                                        }
                                    }
                                }
                            }
                        }
                    }

                }
                catch (Exception e)
                {
                    Log.ConsoleError(e.Message);
                }
            }
        }
        private static void DieMobCommand(CommandArgs args)
        {
            if (args.Parameters.Count > 0 && args.Parameters[0].ToLower() == "reload")
            {
                if (ReadConfig())
                    args.Player.SendMessage("DieMob config reloaded.", Color.BurlyWood);
                else 
                    args.Player.SendMessage("Error reading config. Check log for details.", Color.Red);
                return;
            }
            else if (args.Parameters.Count > 0 && args.Parameters[0].ToLower() == "list")
            {
                for (int r = RegionList.Count - 1; r >= 0; r--)
                {
                    var regManReg = regionManager.GetRegionByName(RegionList[r].TSRegion.Name);
                    if (RegionList[r].TSRegion == null || regManReg == null || regManReg.Name == "")
                    {
                        lock (db)
                        {
                            db.Query("Delete from DieMobRegions where Region = @0 AND WorldID = @1", RegionList[r].RegionName, Main.worldID);
                        }
                        RegionList.RemoveAt(r);
                    }
                }
                int page = 0;
                if (args.Parameters.Count > 1)
                    int.TryParse(args.Parameters[1], out page);
                if (page <= 0)
                    page = 1;
                page = (page - 1) * 6;
                args.Player.SendMessage(String.Format("Displaying DieMob regions {0} - {1}:", page + 1, page + 6), Color.LightSalmon);
                for (int i = page; i < RegionList.Count; i++)
                {
                    if (i < page + 6)
                        args.Player.SendMessage(String.Format("{0} @ X: {1}, Y: {2}", RegionList[i].TSRegion.Name, RegionList[i].TSRegion.Area.X, RegionList[i].TSRegion.Area.Y), Color.BurlyWood);
                }
                return;
            }
            else if (args.Parameters.Count > 1 && args.Parameters[0].ToLower() == "info")
            {
                DieMobRegion reg = GetRegionByName(args.Parameters[1]);
                if (reg == null)
                    args.Player.SendMessage(String.Format("Region {0} not found on DieMob list", args.Parameters[1]), Color.Red);
                else
                {
                    args.Player.SendMessage(String.Format("DieMob region: {0}", args.Parameters[1]), Color.DarkOrange);
                    args.Player.SendMessage(String.Format("Type: {0}", reg.Type.ToString()), Color.LightSalmon);
                    args.Player.SendMessage(String.Format("Affects friendly NPCs: {0}", reg.AffectFriendlyNPCs ? "True" : "False"), Color.LightSalmon);
                    args.Player.SendMessage(String.Format("Affects statue spawned mobs: {0}", reg.AffectStatueSpawns? "True" : "False"), Color.LightSalmon);
                    args.Player.SendMessage(String.Format("Replacing {0} mobs. Type '/dm replacemobsinfo RegionName [pageNum]' to get a list.", reg.ReplaceMobs.Count), Color.LightSalmon);                        
                }
                return;
            }
            else if (args.Parameters.Count > 1 && args.Parameters[0].ToLower() == "replacemobsinfo")
            {
                DieMobRegion reg = GetRegionByName(args.Parameters[1]);
                if (reg == null)
                    args.Player.SendMessage(String.Format("Region {0} not found on DieMob list", args.Parameters[1]), Color.Red);
                else
                {
                    int page = 0;
                    if (args.Parameters.Count > 2)
                        int.TryParse(args.Parameters[2], out page);
                    if (page <= 0)
                        page = 1;
                    int startIndex = (page - 1) * 6;
                    args.Player.SendMessage(String.Format("{0} mob replacements page {1}:", reg.RegionName, page), Color.LightSalmon);
                    for (int i = startIndex; i < reg.ReplaceMobs.Count; i++)
                    {
                        if (i < startIndex + 6)
                        {
                            int key = reg.ReplaceMobs.Keys.ElementAt(i);
                            args.Player.SendMessage(String.Format("[{0}] From: {1}  To: {2}", i+1, key, reg.ReplaceMobs[key]), Color.BurlyWood);
                        }
                    }
                }
                return;
            }
            else if (args.Parameters.Count > 0 && args.Parameters[0].ToLower() == "mod")
            {
                if (args.Parameters.Count > 1)
                {
                    DieMobRegion region = GetRegionByName(args.Parameters[1].ToLower());
                    if (region == null)
                    {
                        args.Player.SendMessage(String.Format("Region {0} not found on DieMob list", args.Parameters[1]), Color.Red);
                        return;
                    }
                    if (args.Parameters.Count > 2)
                    {
                        switch (args.Parameters[2].ToLower())
                        {
                            case "type":
                                {
                                    if (args.Parameters.Count > 3 && (args.Parameters[3].ToLower() == "kill" || args.Parameters[3].ToLower() == "repel" || args.Parameters[3].ToLower() == "passive"))
                                    {
                                        if (args.Parameters[3].ToLower() == "repel")
                                        {
                                            region.Type = RegionType.Repel;
                                            args.Player.SendMessage(String.Format("Region {0} is now repeling mobs", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        else if (args.Parameters[3].ToLower() == "passive")
                                        {
                                            region.Type = RegionType.Passive;
                                            args.Player.SendMessage(String.Format("Region {0} is now passive", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        else
                                        {
                                            region.Type = RegionType.Kill;
                                            args.Player.SendMessage(String.Format("Region {0} is now killing mobs", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        Diemob_Update(region);
                                        return;
                                    }
                                    break;
                                }
                            case "affectfriendlynpcs":
                                {
                                    if (args.Parameters.Count > 3 && (args.Parameters[3].ToLower() == "true" || args.Parameters[3].ToLower() == "false"))
                                    {
                                        if (args.Parameters[3].ToLower() == "true")
                                        {
                                            region.AffectFriendlyNPCs = true;
                                            args.Player.SendMessage(String.Format("Region {0} is now affecting friendly NPCs", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        else
                                        {
                                            region.AffectFriendlyNPCs = false;
                                            args.Player.SendMessage(String.Format("Region {0} is no longer affecting friendly NPCs", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        Diemob_Update(region);
                                        return;
                                    }
                                    break;
                                }
                            case "affectstatuespawns":
                                {
                                    if (args.Parameters.Count > 3 && (args.Parameters[3].ToLower() == "true" || args.Parameters[3].ToLower() == "false"))
                                    {
                                        if (args.Parameters[3].ToLower() == "true")
                                        {
                                            region.AffectStatueSpawns = true;
                                            args.Player.SendMessage(String.Format("Region {0} is now affecting statue spawned mobs", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        else
                                        {
                                            region.AffectStatueSpawns = false;
                                            args.Player.SendMessage(String.Format("Region {0} is no longer affecting statue spawned mobs", region.TSRegion.Name), Color.LightSalmon);
                                        }
                                        Diemob_Update(region);
                                        return;
                                    }
                                    break;
                                }
                            case "replacemobs":
                                {
                                    if (args.Parameters.Count > 4 && (args.Parameters[3].ToLower() == "add" || args.Parameters[3].ToLower() == "del"))
                                    {
                                        int fromMobID, toMobID;
                                        if (args.Parameters[3].ToLower() == "add" && args.Parameters.Count > 5 && int.TryParse(args.Parameters[4], out fromMobID) && int.TryParse(args.Parameters[5], out toMobID))
                                        {
                                            if (region.ReplaceMobs.ContainsKey(fromMobID))
                                            {
                                                args.Player.SendMessage(String.Format("Region {0} already is already converting mobID {1} to mob {2}", region.TSRegion.Name, fromMobID, region.ReplaceMobs[fromMobID]), Color.LightSalmon);
                                                return;
                                            }
                                            region.ReplaceMobs.Add(fromMobID, toMobID);
                                            args.Player.SendMessage(String.Format("Region {0} is now converting mobs with id {1} to mobs {2}", region.TSRegion.Name, fromMobID, toMobID), Color.LightSalmon);
                                            Diemob_Update(region);
                                            return;
                                        }
                                        else if (args.Parameters[3].ToLower() == "del" && int.TryParse(args.Parameters[4], out fromMobID))
                                        {
                                            if (region.ReplaceMobs.ContainsKey(fromMobID))
                                                region.ReplaceMobs.Remove(fromMobID);
                                            args.Player.SendMessage(String.Format("Region {0} is no longer converting mobs with id {1}", region.TSRegion.Name, fromMobID), Color.LightSalmon);
                                            Diemob_Update(region);
                                            return;
                                        }
                                    }
                                    break;
                                }
                        }
                    }
                }
                args.Player.SendMessage("/dm mod RegionName option arguments", Color.DarkOrange);
                args.Player.SendMessage("Options:", Color.LightSalmon);
                args.Player.SendMessage("type - args: kill [default] / repel / passive", Color.LightSalmon);
                args.Player.SendMessage("affectfriendlynpcs - args: true / false [default]", Color.LightSalmon);
                args.Player.SendMessage("affectstatuespawns - args: true / false [default]", Color.LightSalmon);
                args.Player.SendMessage("replacemobs - args: add fromMobID toMobID / del fromMobID", Color.LightSalmon);
                return;
            }
            else if (args.Parameters.Count > 1)
            {
                var region = regionManager.GetRegionByName(args.Parameters[1]);
                if (region != null && region.Name != "")
                {
                    if (args.Parameters[0].ToLower() == "add")
                    {
                        if (RegionList.Select(r => r.TSRegion).Contains(region))
                        {
                            args.Player.SendMessage(String.Format("Region '{0}' is already on the DieMob list", region.Name), Color.LightSalmon);
                            return;
                        }
                        if (!DieMob_Add(region.Name))
                        {
                            args.Player.SendMessage(String.Format("Error adding '{0}' to DieMob list. Check log for details", region.Name), Color.Red);
                            return;
                        }
                        RegionList.Add(new DieMobRegion() { TSRegion = region });
                        args.Player.SendMessage(String.Format("Region '{0}' added to DieMob list", region.Name), Color.BurlyWood);
                        return;
                    }
                    else if (args.Parameters[0].ToLower() == "del")
                    {
                        if (!RegionList.Select(r => r.TSRegion).Contains(region))
                        {
                            args.Player.SendMessage(String.Format("Region '{0}' is not on the DieMob list", region.Name), Color.LightSalmon);
                            return;
                        }
                        DieMob_Delete(region.Name);
                        args.Player.SendMessage(String.Format("Region '{0}' deleted from DieMob list", region.Name), Color.BurlyWood);
                        return;
                    }
                    return;
                }
                else
                {
                    args.Player.SendMessage(String.Format("Region '{0}' not found", args.Parameters[1]), Color.Red);
                    return;
                }
            }
            args.Player.SendMessage("Syntax: /diemob [add | del] RegionName - Creates / Deletes DieMob region based on pre-existing region", Color.LightSalmon);
            args.Player.SendMessage("Syntax: /diemob list [page number] - Lists DieMob regions", Color.LightSalmon);
            args.Player.SendMessage("Syntax: /diemob reload - Reloads config.json file", Color.LightSalmon);
            args.Player.SendMessage("Syntax: /diemob mod RegionName - Modifies a DieMob region", Color.LightSalmon);
            args.Player.SendMessage("Syntax: /diemob info RegionName - Displays info for a DieMob region", Color.LightSalmon);
        }
        private static void DieMob_Read()
        {
            QueryResult reader;
            lock (db)
            {
                reader = db.QueryReader("Select * from DieMobRegions WHERE WorldID = @0", Main.worldID);
            }
            lock (RegionList)
            {
                while (reader.Read())
                {                    
                    var regionName = reader.Get<string>("Region");
                    var region = regionManager.GetRegionByName(regionName);
                    if (region != null && region.Name != "")
                    {
                        RegionList.Add(new DieMobRegion() { TSRegion = region, RegionName = region.Name, AffectFriendlyNPCs = reader.Get<bool>("AffectFriendlyNPCs"), AffectStatueSpawns = reader.Get<bool>("AffectStatueSpawns"), ReplaceMobs = JsonConvert.DeserializeObject<Dictionary<int, int>>(reader.Get<string>("ReplaceMobs")), Type = (RegionType)reader.Get<int>("Type") });
                    }
                    else
                        db.Query("Delete from DieMobRegions where Region = @0 AND WorldID = @1", regionName, Main.worldID);
                }
                reader.Dispose();
            }
        }
        private static bool DieMob_Add(string name)
        {
            lock (db)
            {
                db.Query("INSERT INTO DieMobRegions (Region, WorldID, AffectFriendlyNPCs, AffectStatueSpawns, Type, ReplaceMobs) VALUES (@0, @1, 0, 0, 0, @2)", name.ToLower(), Main.worldID, JsonConvert.SerializeObject(new Dictionary<int,int>()));
            }
            return true;
        }
        private static void DieMob_Delete(String name)
        {
            lock (db)
            {
                db.Query("Delete from DieMobRegions where Region = @0 AND WorldID = @1", name.ToLower(), Main.worldID);
            }
            lock (RegionList)
            {
                for (int i = RegionList.Count - 1; i >= 0; i--)
                {
                    if (RegionList[i].RegionName.ToLower() == name.ToLower())
                    {
                        RegionList.RemoveAt(i);
                        break;
                    }
                }
            }
        }
        private static void Diemob_Update(DieMobRegion region)
        {
            lock (db)
            {
                db.Query("UPDATE DieMobRegions SET AffectFriendlyNPCs = @2, AffectStatueSpawns = @3, Type = @4, ReplaceMobs = @5 where Region = @0 AND WorldID = @1", region.TSRegion.Name.ToLower(), Main.worldID, region.AffectFriendlyNPCs, region.AffectStatueSpawns, (int)region.Type, JsonConvert.SerializeObject(region.ReplaceMobs));
            }
        }
        private static DieMobRegion GetRegionByName(string name)
        {
            foreach (DieMobRegion reg in RegionList)
            {
                if (reg.TSRegion.Name.ToLower() == name.ToLower())
                    return reg;
            }
            return null;
        }
    }


}
