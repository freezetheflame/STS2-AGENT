# STS2 AI Agent

杀戮尖塔2 的 AI 策略助手 — 通过 mod 导出完整游戏状态，交给 LLM 做决策分析。

## 项目结构

```
STS2-AGENT/
├── mod/                       # C# Mod (Godot .NET)
│   ├── STS2Agent.json         # Mod 元数据
│   ├── STS2Agent.csproj       # .NET 项目文件
│   └── Scripts/
│       ├── Entry.cs           # Mod 入口
│       ├── AgentTelemetry.cs  # 事件监听 + HTTP 发送
│       └── GameStateExtractor.cs  # 游戏状态提取
├── server/                    # Python 桥接服务器
│   ├── main.py                # FastAPI 服务器
│   └── requirements.txt
└── logs/                      # 运行时日志（不提交）
```

## 架构

```
STS2 游戏 + Mod  →  POST /event →  Python 服务器  →  终端实时输出
                  (游戏状态JSON)   (localhost:8765)    (后续接入 LLM)
```

## Windows 端环境搭建

### 1. 安装工具

- **[Godot 4.5.1 Mono (.NET版)](https://godotengine.org/download/)** — 必须 .NET 版！
- **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)**
- **[Rider](https://www.jetbrains.com/rider/)** （强烈推荐）或 VS Code + C# Dev Kit
- **[STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)** — 下载 Release 的 DLL 放到 `Slay the Spire 2/mods/STS2-RitsuLib/` 目录

### 2. 克隆项目

```bash
git clone https://github.com/freezetheflame/STS2-AGENT.git
```

### 3. 修改 .csproj 路径

打开 `mod/STS2Agent.csproj`，把 `<Sts2Dir>` 改成你的 STS2 安装路径：

```xml
<Sts2Dir>D:\Steam\steamapps\common\Slay the Spire 2</Sts2Dir>
```

### 4. 构建 Mod

```bash
cd mod
dotnet build
```

构建成功后，DLL 和 JSON 会自动复制到 STS2 的 `mods/STS2Agent/` 目录。

### 5. 启动桥接服务器

```bash
cd server
pip install -r requirements.txt
python main.py
```

### 6. 启动游戏测试

1. 启动 STS2，确认右下角显示"已加载模组"
2. 开一局游戏，进入战斗
3. 观察服务器终端是否打印游戏状态

## 开发迭代

每次改代码后：

```bash
# Windows 上
cd mod && dotnet build

# WSL 上（服务器）
cd server && python main.py
```

游戏不需要重启 — 下次进入战斗就会加载新代码。

## 当前阶段

Phase 1: 数据导出 ✅ — Mod 提取战斗状态并发送到 Python 服务器
Phase 2: LLM 集成 🚧 — 将状态发给 GPT/Claude 做策略分析
Phase 3: 游戏内反馈 🚧 — 通过通知/按钮在游戏中显示 AI 建议

## 许可

MIT
