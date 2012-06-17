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
    [APIVersion(1, 12)]
    public class DieMobMain : TerrariaPlugin
    {
        private static IDbConnection db;
        private static string savepath = Path.Combine(TShock.SavePath, "DieMob/");
        private static bool initialized = false;
        private static List<Region> RegionList = new List<Region>();
        private static DateTime lastUpdate = DateTime.UtcNow;
        private static Config config;
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
            get { return new Version("0.15"); }
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
            Commands.ChatCommands.Add(new Command("diemob", DieMobCommand, "diemob", "DieMob", "dm"));

        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GameHooks.Update -= OnUpdate;
            }
            base.Dispose(disposing);
        }
        private void SetupDb()
        {
            if (TShock.Config.StorageType.ToLower() == "sqlite")
            {
                string sql = Path.Combine(savepath, "DieMob.sqlite");
                db = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
            }
            else if (TShock.Config.StorageType.ToLower() == "mysql")
            {
                try
                {
                    var hostport = TShock.Config.MySqlHost.Split(':');
                    db = new MySqlConnection();
                    db.ConnectionString =
                        String.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                                      hostport[0],
                                      hostport.Length > 1 ? hostport[1] : "3306",
                                      TShock.Config.MySqlDbName,
                                      TShock.Config.MySqlUsername,
                                      TShock.Config.MySqlPassword
                            );
                }
                catch (MySqlException ex)
                {
                    Log.Error(ex.ToString());
                    throw new Exception("MySql not setup correctly");
                }
            }
            else
            {
                throw new Exception("Invalid storage type");
            }
            SqlTableCreator SQLcreator = new SqlTableCreator(db, db.GetSqlType() == SqlType.Sqlite ? (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());
            var table = new SqlTable("DieMobRegions",
             new SqlColumn("Region", MySqlDbType.Text) { Primary = true, Unique = true, Length = 30 },
             new SqlColumn("WorldID", MySqlDbType.Int32)
            );
            SQLcreator.EnsureExists(table);
        }

        class Config
        {
            public bool KillFriendly = false;
            public bool KillStatueSpawns = false;
            public Config(bool kF, bool kSS)
            {
                this.KillFriendly = kF;
                this.KillStatueSpawns = kSS;
            }

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
                        config = new Config(false, false);
                        var configString = JsonConvert.SerializeObject(config, Formatting.Indented);
                        sr.Write(configString);
                    }
                    stream.Close();
                }
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.Message);
                config = new Config(false, false);
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
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= 1000)
            {
                lastUpdate = DateTime.UtcNow;
                if (!initialized && Main.worldID > 0)
                {
                    initialized = true;
                    OnWorldLoad();
                }
                try
                {
                    for (int i = 0; i < Main.npc.Length; i++)
                    {
                        if (Main.npc[i].active)
                        {
                            if ((Main.npc[i].friendly && config.KillFriendly) || (!Main.npc[i].friendly && (Main.npc[i].value > 0 || config.KillStatueSpawns)))
                            {
                                foreach (Region reg in RegionList)
                                {
                                    if (reg.InArea((int)(Main.npc[i].position.X / 16), (int)(Main.npc[i].position.Y / 16)))
                                    {
                                        Main.npc[i].netDefaults(0);
                                        TSPlayer.Server.StrikeNPC(i, 99999, 90f, 1);
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
            if (args.Parameters.Count > 0 && args.Parameters[0] == "reload")
            {
                if (ReadConfig())
                    args.Player.SendMessage("DieMob config reloaded.", Color.BurlyWood);
                else 
                    args.Player.SendMessage("Error reading config. Check log for details.", Color.Red);
                return;
            }
            else if (args.Parameters.Count > 0 && args.Parameters[0] == "list")
            {
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
                        args.Player.SendMessage(String.Format("{0} @ X: {1}, Y: {2}", RegionList[i].Name, RegionList[i].Area.X, RegionList[i].Area.Y), Color.BurlyWood);
                }
                return;
            }
            else if (args.Parameters.Count > 1)
            {
                var region = TShock.Regions.GetRegionByName(args.Parameters[1]);
                if (region != null && region.Name != "")
                {
                    if (args.Parameters[0] == "add")
                    {
                        if (RegionList.Contains(region))
                        {
                            args.Player.SendMessage(String.Format("Region '{0}' is already on the DieMob list", region.Name), Color.LightSalmon);
                            return;
                        }
                        if (!DieMob_Add(region.Name))
                        {
                            args.Player.SendMessage(String.Format("Error adding '{0}' to DieMob list. Check log for details", region.Name), Color.Red);
                            return;
                        }
                        RegionList.Add(region);
                        args.Player.SendMessage(String.Format("Region '{0}' added to DieMob list", region.Name), Color.BurlyWood);
                        return;
                    }
                    else if (args.Parameters[0] == "del")
                    {
                        if (!RegionList.Contains(region))
                        {
                            args.Player.SendMessage(String.Format("Region '{0}' is not on the DieMob list", region.Name), Color.LightSalmon);
                            return;
                        }
                        DieMob_Delete(region);
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
            args.Player.SendMessage("Syntax: /diemob [add | del] \"region name\" - Creates / Deletes DieMob region based on pre-existing region", Color.LightSalmon);
            args.Player.SendMessage("Syntax: /diemob list [page number] - Lists DieMob regions", Color.LightSalmon);
            args.Player.SendMessage("Syntax: /diemob reload - Reloads config.json file", Color.LightSalmon);
        }
        private static void DieMob_Read()
        {
            QueryResult reader;
            lock (db)
            {
                reader = db.QueryReader("Select Region from DieMobRegions WHERE WorldID = @0", Main.worldID);
            }
            lock (RegionList)
            {
                while (reader.Read())
                {
                    var region = TShock.Regions.GetRegionByName(reader.Get<string>("Region"));
                    if (region != null && region.Name != "")
                        RegionList.Add(region);
                }
                reader.Dispose();
            }
        }
        private static bool DieMob_Add(string name)
        {
            lock (db)
            {
                db.Query("INSERT INTO DieMobRegions (Region, WorldID) VALUES (@0, @1)", name, Main.worldID);
            }
            return true;
        }
        private static void DieMob_Delete(Region region)
        {
            lock (db)
            {
                db.Query("Delete from DieMobRegions where Region = @0 AND WorldID = @1", region.Name, Main.worldID);
            }
            lock (RegionList)
            {
                RegionList.Remove(region);
            }
        }
    }


}
