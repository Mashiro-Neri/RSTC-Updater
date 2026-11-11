using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace RstcUpdater
{
    /// <summary>V1 配置：%APPDATA%\RSTC-Updater\config.json</summary>
    internal sealed class AppConfig
    {
        public string LastGameDir;
        public string LastInstance;
        public int BackupRetention = 3;

        public static string ConfigDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "RSTC-Updater");
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "config.json"); }
        }

        public static AppConfig Load()
        {
            var config = new AppConfig();
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    return config;
                }

                var root = JsonNode.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8)) as JsonObject;
                if (root == null)
                {
                    return config;
                }

                config.LastGameDir = GetString(root, "lastGameDir");
                config.LastInstance = GetString(root, "lastInstance");
                config.BackupRetention = GetInt(root, "backupRetention", 3);
            }
            catch (Exception)
            {
                // 配置损坏时用默认值，不阻断
            }

            return config;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var root = new JsonObject
                {
                    ["lastGameDir"] = LastGameDir,
                    ["lastInstance"] = LastInstance,
                    ["backupRetention"] = BackupRetention,
                    ["savedAt"] = DateTime.Now.ToString("o")
                };
                File.WriteAllText(ConfigPath, root.ToJsonString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        private static string GetString(JsonObject root, string key)
        {
            return root[key] == null ? null : root[key].GetValue<string>();
        }

        private static int GetInt(JsonObject root, string key, int fallback)
        {
            try
            {
                return root[key] == null ? fallback : root[key].GetValue<int>();
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
