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

## 编译与验证说明

已安装 .NET 8 SDK，`tools/BuildCheck/` 下按 21 个 asmdef 生成了对应的 csproj，**全部 21 个程序集实际编译通过**。`tools/SmokeTests/` 是关键契约测试（11 项：重复结算、中途失败回滚、剧情重复选择、跨节点选项、战损继承、快照深拷贝、随机源上界、内容校验、内容驱动奖励、航行事务）。

本机应用控制策略阻止新生成 DLL 的加载，测试**编译通过但未能执行**。换一台允许执行的机器：

```bash
cd tools/SmokeTests && dotnet run
```

Unity 侧：用 Unity 2021.3 打开 `game/` 目录。`StarPawn.Presentation` 目前无源文件，等表现层填充。

## 审查修复记录（2026-11-22）

按代码审查意见完成六项修复，骨架从"静态检查通过"变成"可编译、可验证、失败后状态正确"：

**1. 编译阻断** — `HashSet<EntityId>` 替代 `battleId.Value`；两个 `SquadRole` 枚举保留在各自模块，应用层显式映射（`GameBootstrap` 的 asmdef 也补上了缺失的 Contracts 引用）。

**2. 战斗契约** — `FleetEntry` 带已解析属性与继承战损；`BattleResult` 返回逐舰战后事实；新增 `ObstacleSnapshot`（静止有血量掩体）和 `PowerAllocationCommand`（三档预设）；`IBattleSession` 让表现层通过门面推进战斗；契约顶部写明"二维平面仿真 + 3D 表现"的空间约定。

**3. 正确性** — `Foundation` 加 `IStateCheckpoint`，`Atomic()` 在动作前给六个服务打检查点，任一步失败全量回滚（含门面自己的已结算标记）。剧情改为 `Choose` 只校验、`CommitChoice` 才推进，杜绝重复发奖和跨节点选择。所有假实现的 `Read()` 改为深拷贝或重建，外部改快照不影响内部状态。

**4. 内容通路** — `BasicContentValidator` 真正读 JSON，校验 ID 唯一、剧情/任务/星图/舰船引用；补齐 `ship.d_destroyer`、`faction.empire`、`ch1.conflict` 三处悬空引用；奖励改由 `JsonMissionRewardSource` 从 `content/missions/` 读取，改 JSON 会改变结算数值。

**5. 确定性清理** — 关系键统一为 `from|to|kind`；经济拒绝负数与溢出；`PerformPoliticalAction` 真正执行；`SettleBattle` 纳入 `EvaluatePromotion`；`DeterministicRandom` 修零种子退化与浮点上界问题。

**6. 战损闭环** — `ShipReadiness`（Ready/Damaged/InRepair/Destroyed）决定出战资格；战斗继承长期损伤，逐舰回写；击沉舰退出舰队，下一场不会满血复活。

### 仍需注意

- **事务用回滚而非真正的候选状态隔离**：单机同步执行且无并发观察者，回滚等价于原子性。将来若引入异步或多线程，必须换成真正的候选状态提交。
- **空间维度**：仿真是二维平面。若要做完整三维机动，需改 `SpawnPoint`/Snapshot 坐标和命令契约。
- **内容校验器是最小实现**：覆盖当前 6 类配置，新增配置类型时需扩展 `SubDirs` 和引用检查。
- **任务日志运行时**：`content/quests/` 配置已就位，但运行时服务（读配置、算状态、出导航）尚未实现，见 `docs/storyline.md`。

