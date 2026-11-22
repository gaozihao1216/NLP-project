# 框架骨架说明

这套骨架按 `development-plan.md` 的建议落地，目的是让 A–H 八个个工作包能同时开工。**当前只有契约和假实现，没有真实业务逻辑**——先把边界固定下来，再往里填东西。

## 已建成的东西

```
game/
  Packages/                        12 个 Unity 包（模块边界）
    com.starpawn.foundation/         ID、时间、随机源、Result、错误码
    com.starpawn.{模块}/Runtime/
      Contracts/                    对外接口与数据结构（属于本模块）
      Domain/                       内部规则实现（当前为假实现）
    com.starpawn.application/       工作流与门面 CampaignFacade
    com.starpawn.infrastructure/    内容校验、存档
    com.starpawn.presentation/      表现层（待 Unity 侧填充）
  Assets/Game/Bootstrap/            唯一知道所有具体实现的地方
content/
  ships/ factions/ star-maps/       舰船数值、阵营策略、星图节点
  missions/ stories/                战斗关卡、剧情节点
  quests/                           任务日志条目 + 日志视图配置
docs/
  development-plan.md               GPT 提出的并行开发规划原文
  storyline.md                      剧情线与任务日志设计（单一事实来源）
```

一共 21 个 `.asmdef`。纯 C# 包（Foundation、各 Contracts/Domain、Application、Infrastructure）全部设 `noEngineReferences: true`，编译期就无法引用 UnityEngine——这是"规则与表现分离"的硬约束，不靠口头约定。

## 并行开发怎么开始

每个包目前都能独立演示：

| 工作包 | 现状 | 下一步 |
|---|---|---|
| A 战斗仿真 | `FakeCombatRuntime` 可按固定 spec 跑完整战斗 | 替换为真实运动/弹道/命中，保持 `ICombatRuntime` 不变 |
| B 战斗表现 | 无 Unity 代码，契约已定 | 读 `BattleSnapshot`，消费 `BattleEvent` |
| C 战略规则 | `Fake*Service` 各司其职 | 逐个替换真实现，接口不变 |
| D 关系政治 | `FakeRelationsService` 已能校验政治行为 | 填真实关系规则 |
| E 任务剧情 | `FakeNarrativeRuntime` 有两个节点与有后果的选择 | 扩产剧情内容，不改接口 |
| F 战略界面 | 待填 | 只调 `CampaignFacade`，不碰 Domain |
| G 星零副官 | `RuleBasedOfficer` 三种能力已分离 | LLM 对话异步接入，加超时降级 |
| H 基础设施与集成 | `GameBootstrap` 已装配出完整假闭环 | 补内容加载、存档、场景切换 |

**替换假实现的唯一位置是 `GameBootstrap.Create()`**。改那一个方法，其他代码不用动。

## 三条不能破的规则

1. **业务模块之间不互相调用实现。** `Application` 引用各模块的 `Contracts`；`Domain` 只引用自己的 `Contracts` 和 `Foundation`。asmdef 已经在编译期卡死跨模块引用。
2. **战斗只接收准备好的输入。** `BattleStartSpec` 里没有军衔、剧情、经济对象。战斗结束时只返回 `BattleResult` 事实，奖励多少由 `SettleBattle` 工作流决定。改成长曲线不需要动战斗代码。
3. **剧情只提效果请求。** `StoryDecision` 返回 `StoryEffect[]`，由应用层分派给对应模块校验执行。剧情拿不到关系图的内部对象。

`SettleBattle` 已经落实三条结算约束：用 `BattleId` 防重复发奖；先在候选阶段验证；全部提交成功后才让剧情读新事实。

## 第一次联调

没有 Unity 也能验证的纯 C# 闭环：

```
GameBootstrap.Create()  →  CampaignFacade
  LaunchBattle()        →  战斗 spec
  （Step 直到出结果）
  SettleBattle()        →  舰队/资源/军功/关系依次更新
  ChooseStoryOption()   →  剧情效果分派执行
  ReadStory()           →  推进到新节点
```

把假战斗的 `TryGetResult` 换成真实现，就是第一轮整合。

## 已定决策（原四条待定问题）

四条已由团队拍板。每条写清决策、理由和落点。**注意：部分是设计决策，代码还没跟进**，见文末待办。

### 1. 战斗期间的战略时间：暂停

战斗开始时冻结战略层时间，视整场交火为瞬间发生。理由是现实战场上直接交锋时间占比很低，大部分时间处于对峙；把战略层冻结，玩家的注意力才能集中在操舵和瞄准上。

- **落点**：现有假实现已是暂停，无需改代码。写剧情时不要出现"激战三天三夜"这类台词，战斗在叙事上按一次短暂接舷战处理。
- **连带**：战斗结算产出的不只是战损，还包括**管辖领域扩张**（`Navigation`）和**战略资源掠夺**（`Economy`）。这两项是结算工作流的后果，不是战斗模块自己算的——`BattleResult` 仍只返回战损事实，`SettleBattle` 负责分派。

### 2. 坐标单位与仿真参数：星图和战斗分层

| 层 | 单位 | 类型 | 说明 |
|---|---|---|---|
| 星图（`Navigation`） | 光分 | `long` | 绝对坐标，总尺度 1000 光年 = 5.26 亿光分，`long` 装得下且预留银河系扩展 |
| 战斗（`Combat`） | 公里 | `float` | 局部坐标，以战场中心为原点，范围几十到几万公里 |
| 显示层 | 任意 | — | 比例尺随缩放动态换算，纯表现 |

换算常量 `1 光分 = 17,987,547 公里` 放 `Foundation`，只在进入战斗时换算一次。战斗物理不在银河系绝对坐标里算——否则浮点精度会被吃掉，数值表也会变成一坨科学计数法。

- 仿真步长仍为 `1/30` 秒。
- 场景内的陨石块按 `BattleStartSpec.RandomSeed` 程序化生成，优先用 `Foundation` 里的 `DeterministicRandom`，保证可复现；存档只存 seed，不存每块的状态。
- 内容版本号与存档版本号尚未单独定，先用 `0.1.0` 占位。

### 3. 舰船战斗参数：立方律骨架 + 小说锚点 + 定位微调

立方律决定量级：结构量与火力随舰体特征长度 L 三次方增长。但小说第 34 章的表不是立方关系（A 级巡逻艇到 G 级巡洋舰，乘员差 30 倍、主炮值只差 5 倍），说明大舰靠炮塔数量而非单炮口径堆火力。所以不能直接用立方律覆盖小说数值。

```
HP       = baseHP  × (L / L_ref)³ × roleFactor
主炮伤害  = baseGun × (L / L_ref)³ × 炮塔系数（取自小说表主炮值）
副炮伤害  = 主炮 × 0.3~0.6（按舰种定位）
能量池    = baseEnergy × (L / L_ref)³ × roleFactor
```

`roleFactor` 按定位取：主力舰 1.0、护航舰 0.8、侦察舰 0.6。L 按每级舰手填特征长度。

立方律的副作用是 4 倍长度 = 64 倍战力，大舰会碾压小舰（符合小说，但让数量优势失效）。补偿手段是小舰的机动与精度：转向更快、散布更小、能贴身打薄弱区。这两个参数独立于火力，不能省。

**新增战斗机制：能量三路分配。** 小说原文有明确设定——第 20 章"所有主能量用于补充防护罩"、第 22 章护罩 100% 能扛 C 级驱逐舰一发主炮而 20% 连巡逻艇齐射都扛不住、第 45 章"把防护罩的能源调给镭射炮增加威力"。据此：

- 推进 / 护罩 / 武器共用一个能量池，投入护罩的能量决定护罩强度
- 护罩平时关闭（耗能为由，原文第 22 章），激战开启
- 操作用三档预设（航行 / 均衡 / 交战），不做实时滑条——原作也是靠一句口令切换，且实时战斗中操舵、瞄准、分队指令已够占手

**陨石修正**：不是障碍物，而是**有血量的静止掩体**。炮弹先打陨石，打碎后暴露后方目标；完全静止，无速度无物理翻滚。尺寸按显示需求放大，不追求写实比例（现实中陨石多与舰体相当，但那样游戏里看不清）。

### 4. 阵营差异：配置驱动，能力矩阵暂不细化

三阵营共用一套规则，差异由配置承担。当前已有联邦与家族配置，银鹰需补最简配置。最初的"阵营能力矩阵"设想（火力/机动/单位三维）先记录不做——第一版只有一个阵营的完整剧情，矩阵没有验证场景，提前定值是空想。

资源托管策略：

| 阵营 | 成长资源 | 资源可见度 | 补给支付方 | 关系预设 | 剧情入口 |
|---|---|---|---|---|---|
| 万罗联邦 | 军功 | 仅作战信息 | 高层代管 | 高猜疑（陈宇 -70） | `tanglong` |
| 银鹰帝国 | 军功 | 仅作战信息（暂定） | 高层代管 | 表面忠诚、暗地派系（待细化） | 待定 |
| 家族势力 | 资源膨胀 | 全部 | 自付 | 家臣忠诚 | `sandbox` |

后期玩家升入高层后，资源面板从隐藏切到可见——切换点绑定**军衔 ID**，具体绑哪一级等成长系统定稿时再填，不在 UI 里另写逻辑。

叙事侧的落实放在 `docs/storyline.md` 和 `content/quests/`：任务日志按主线/支线/委托分组，支持单条追踪和星图导航引导。银鹰与家族的任务条目目前只是配置示例，用于验证共享架构，不含完整剧情。

## 决策已定但代码未跟进（待办）

上面四条是设计决策，下面这些还没有写进 C# 契约与假实现。接手时按此清单补，别当成已实现：

| 决策 | 待补内容 | 涉及文件 |
|---|---|---|
| 战斗期间战略暂停 | 已符合，无需改 | — |
| 结算含扩张与掠夺 | `SettleBattle` 增加 `Navigation` 领地更新；`BattleResult` 加目标达成标记 | `CampaignFacade.cs`、`CombatContracts.cs` |
| 坐标分层 | `Foundation` 加光分/公里换算常量；星图坐标类型定 `long` | `Foundation.cs`、`NavigationContracts.cs` |
| 陨石种子生成 | `BattleStartSpec` 已有 `RandomSeed`，补 `BattlefieldLayout` 取值约定 | `CombatContracts.cs` |
| 陨石静止掩体 | `BattleSnapshot` 加只读 `ObstacleSnapshot[]`（含 `Health01`）；炮弹命中判定 | `CombatContracts.cs`、`FakeCombatRuntime.cs` |
| 能量三路分配 | 加 `PowerAllocationCommand`；`ShipSnapshot` 加护罩值与分配比例；三档预设常量 | `CombatContracts.cs`、`FakeCombatRuntime.cs` |
| 立方律参数 | `content/ships/` 各舰补特征长度、roleFactor、机动/精度参数 | `content/ships/*.json` |
| 银鹰配置 | 补 `content/factions/empire.json` | `content/factions/` |
| 任务日志 | 运行时任务日志服务（读 `content/quests/`、算状态、出导航） | 待定新包 |

## 编译说明

本机没有装 Unity 和 .NET SDK，这批 C# 只做了静态检查（括号配对、命名空间、接口与实现一致性），**没有实际编译**。拿到 Unity 2021.3 后打开 `game/` 目录即可编译；如有编译错误，优先检查 `using` 与 asmdef 引用是否匹配。
