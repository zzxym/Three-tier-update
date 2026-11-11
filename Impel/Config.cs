using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Impel
{
    public static class ConfigService
    {
        public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string ConfigPath => Path.Combine(ExeDir, "Impel.json");

        public static ImpelConfig Load()
        {
            ImpelConfig cfg = null;
            try { if (File.Exists(ConfigPath)) cfg = JsonConvert.DeserializeObject<ImpelConfig>(File.ReadAllText(ConfigPath)); }
            catch { }
            if (cfg == null) cfg = Default();
            // 兜底缺省对象，避免反序列化时子对象为 null
            cfg.server = cfg.server ?? new ServerCfg();
            cfg.dock = cfg.dock ?? new DockCfg();
            cfg.p2p = cfg.p2p ?? new P2PCfg();
            cfg.apps = cfg.apps ?? new List<AppCfg>();
            cfg.installed = cfg.installed ?? new Dictionary<string, string>();
            // 稳定的客户端标识：Repel 按 cid 定向下发升级指令；持久化到 Impel.json，重启后保持不变
            if (string.IsNullOrWhiteSpace(cfg.clientId))
            {
                cfg.clientId = Guid.NewGuid().ToString("N");
                try { Save(cfg); } catch { }
            }
            return cfg;
        }

        static readonly object _saveLock = new object();
        public static void Save(ImpelConfig cfg)
        {
            // 串行化保存：避免更新写 installed 与拖拽吸附写 dock.side 并发时相互截断/丢失更新
            lock (_saveLock) File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
        }

        public static ImpelConfig Default()
        {
            var c = new ImpelConfig();
            c.server.host = "http://gameserver";
            c.server.token = "";   // 不再内置默认口令：部署时必须在设置中填写，避免随代码泄露共享密钥
            c.dock.autohide = false;
            c.dock.side = "right";   // 无配置文件时默认右侧停靠
            c.p2p.enabled = true;
            c.p2p.port = 45123;
            c.softwareRoot = "d:\\soft";   // 与 Models.cs 字段缺省一致：无配置时安装在 d:\soft
            // apps 列表不再内置：运行时由 Repel 的 repel.lst 统一下发
            return c;
        }

        public static string SoftwareRoot(ImpelConfig cfg)
        {
            var b = cfg.softwareRoot ?? "d:\\";
            return Path.IsPathRooted(b) ? b : Path.Combine(ExeDir, b);
        }
        /// <summary>应用本地目录名：优先用中文真实目录名 dir，回退 name，最后回退 id。</summary>
        public static string AppDirName(AppCfg app)
        {
            if (!string.IsNullOrEmpty(app.dir)) return app.dir;
            return string.IsNullOrEmpty(app.name) ? app.id : app.name;
        }
        public static string AppRoot(ImpelConfig cfg, AppCfg app) => Path.Combine(SoftwareRoot(cfg), AppDirName(app));
    }
}
