# 框架骨架说明

这套骨架按 `development-plan.md` 的建议落地，目的是让 A、B 两人各守一边边界并行开工：A 负责战斗仿真与表现，B 负责战略规则、剧情与整合。**当前只有契约和假实现，没有真实业务逻辑**——先把边界固定下来，再往里填东西。

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

一共 22 个 `.asmdef`。纯 C# 包（Foundation、各 Contracts/Domain、Combat.Content、Application、Infrastructure）全部设 `noEngineReferences: true`，编译期就无法引用 UnityEngine——这是"规则与表现分离"的硬约束，不靠口头约定。

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

两人怎么切：A 只改 `com.starpawn.combat`，B 改其余包与 `com.starpawn.application`。
交界只有两处——`BattleStartSpec`（B 产出、A 消费）与 `CampaignFacade.SettleBattle`（A 的结果回传、B 执行）。
`BattleRules.ValidateSpec` / `ValidatePower` 是共用的校验入口，不要在两边各写一套。

## 三条不能破的规则

1. **业务模块之间不互相调用实现。** `Application` 引用各模块的 `Contracts`；`Domain` 只引用自己的 `Contracts` 和 `Foundation`。asmdef 已经在编译期卡死跨模块引用。
2. **战斗只接收准备好的输入。** `BattleStartSpec` 里没有军衔、剧情、经济对象。战斗结束时只返回 `BattleResult` 事实，奖励多少由 `SettleBattle` 工作流决定。改成长曲线不需要动战斗代码。
3. **剧情只提效果请求。** `StoryDecision` 返回 `StoryEffect[]`，由应用层分派给对应模块校验执行。剧情拿不到关系图的内部对象。

`SettleBattle` 落实四条结算约束：用 `BattleId` 防重复发奖；奖励任务 ID 取自 battle spec，不受门面当时状态影响；舰队损伤、资源、军功、关系、星系管辖、残骸缴获与剧情推进全在同一事务里；任一步失败全量回滚且可重试。

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

已安装 .NET 8 SDK，`tools/BuildCheck/` 下按 22 个 asmdef 生成了对应的 csproj，**全部 22 个程序集实际编译通过**。`tools/SmokeTests/` 是关键契约测试（28 个场景块：契约与回滚流程、三维运动与提前量、能源耗尽与滞回、护盾判面/过载/破盾/回充、全舰记账、配置加载与内置镜像一致性）。

本机应用控制策略阻止新生成 DLL 的加载，测试**编译通过但未能执行**。换一台允许执行的机器：

```bash
cd tools/SmokeTests && dotnet run
```

Unity 侧：用 Unity 2021.3 打开 `game/` 目录。`StarPawn.Presentation` 目前无源文件，等表现层填充。

## 两人并行前的底座加固（2026-11-23）

先确认编译：22 个 BuildCheck 工程与 SmokeTests 工程全部实际编译通过。随后补了三处：

**1. 战斗契约自包含与共享校验。** `BattleStartSpec` 增加 `MissionId`、`OriginSystemId`、`TargetSystemId`，结算不再依赖门面当时的可变字段；`FleetEntry.SquadIndex` 限定 0..3。新增 `BattleRules.ValidateSpec` / `ValidatePower`，Application 与 Combat 共用同一份规则——四分队上限、负能量投入、重复舰 ID、非法战损都在进入战斗前拦下。`TryGetResult` 改为深拷贝，调用方改数组不会污染内部结果。

**2. 结算事务补全。** `Atomic` 去掉没用的操作 ID，把门面自己的结算标记、记账与当前 ID 也纳入检查点，并加重入保护（嵌套调用由最外层统一回滚）。剧情 `Evaluate` 改为返回 `CommandResult`，移进事务内——剧情推进失败会连带回滚资源、军功、关系与星系管辖，重试可以成功。

**3. 胜利后果落地。** 架构里承诺的领域扩张与战略缴获现在真的执行：胜利时在同一事务内调用 `Navigation.ClaimSystem`，并按击沉数发放合金。Navigation 的快照与检查点也改成深拷贝，外部改快照不影响内部状态。

本机曾长期无法执行新生成的二进制：Windows 11 的智能应用控制（Smart App Control）处于强制模式，
锁的是**文件**而不是进程——`StarPawn.SmokeTests.exe` 与 `StarPawn.Application.dll` 都在
`Microsoft-Windows-CodeIntegrity/Operational` 里留下 3033/3077 事件（策略
`{0283ac0f-fff1-49ae-ada1-8a933130cad6}`），换 `UseAppHost=false`、走 `dotnet xxx.dll`、加自签名
全都不管用。2026-11-26 关闭该功能后 `cd tools/SmokeTests && dotnet run` 正常，33 块 122 条断言全绿。
仓库内的 `.github/workflows/smoke.yml` 把这件事搬到 ubuntu 上做，任何人本机环境再出问题都不影响验证。

## 审查修复记录（2026-11-22）

按代码审查意见完成六项修复，骨架从"静态检查通过"变成"可编译、可验证、失败后状态正确"：

**1. 编译阻断** — `HashSet<EntityId>` 替代 `battleId.Value`；两个 `SquadRole` 枚举保留在各自模块，应用层显式映射（`GameBootstrap` 的 asmdef 也补上了缺失的 Contracts 引用）。

**2. 战斗契约** — `FleetEntry` 带已解析属性与继承战损；`BattleResult` 返回逐舰战后事实；新增 `ObstacleSnapshot`（静止有血量掩体）和 `PowerAllocationCommand`（三档预设）；`IBattleSession` 让表现层通过门面推进战斗。（当时契约写的是"二维平面仿真 + 3D 表现"，已于 2026-11-25 整体改为三维，见下文。）

**3. 正确性** — `Foundation` 加 `IStateCheckpoint`，`Atomic()` 在动作前给六个服务打检查点，任一步失败全量回滚（含门面自己的已结算标记）。剧情改为 `Choose` 只校验、`CommitChoice` 才推进，杜绝重复发奖和跨节点选择。所有假实现的 `Read()` 改为深拷贝或重建，外部改快照不影响内部状态。

**4. 内容通路** — `BasicContentValidator` 真正读 JSON，校验 ID 唯一、剧情/任务/星图/舰船引用；补齐 `ship.d_destroyer`、`faction.empire`、`ch1.conflict` 三处悬空引用；奖励改由 `JsonMissionRewardSource` 从 `content/missions/` 读取，改 JSON 会改变结算数值。

**5. 确定性清理** — 关系键统一为 `from|to|kind`；经济拒绝负数与溢出；`PerformPoliticalAction` 真正执行；`SettleBattle` 纳入 `EvaluatePromotion`；`DeterministicRandom` 修零种子退化与浮点上界问题。

**6. 战损闭环** — `ShipReadiness`（Ready/Damaged/InRepair/Destroyed）决定出战资格；战斗继承长期损伤，逐舰回写；击沉舰退出舰队，下一场不会满血复活。

### 仍需注意

- **事务用回滚而非真正的候选状态隔离**：单机同步执行且无并发观察者，回滚等价于原子性；嵌套调用由最外层统一打检查点。将来若引入异步或多线程，必须换成真正的候选状态提交。
- **战斗契约有唯一校验入口**：`BattleRules.ValidateSpec` / `ValidatePower` 由 Application 与 Combat 共用。扩展分队数量或能量规则时只改这里，不要在两人各自的代码里另写判断。
- **空间维度**：仿真已是三维（2026-11-25）。`SpawnPoint` 带 z/pitch/roll，`HelmCommand` 给四通道，快照带位置/姿态/速度/角速度。二维时代的 `Heading` 字段已删除，表现层改用 `Yaw/Pitch/Roll`（与 Unity 的 `Quaternion.Euler(pitch, yaw, roll)` 同约定）。
- **内容校验器是最小实现**：覆盖当前 6 类配置，新增配置类型时需扩展 `SubDirs` 和引用检查。
- **任务日志运行时**：`content/quests/` 配置已就位，但运行时服务（读配置、算状态、出导航）尚未实现，见 `docs/storyline.md`。


## 真实战斗仿真：三维（2026-11-25）

`com.starpawn.combat/Domain` 里落了第一版真仿真 `CombatRuntime`，`FakeCombatRuntime` 保留给战略流程与契约测试。
`GameBootstrap.Create()` 已切到真实现，切回去只改那一行。

目录：

```text
Domain/
  Vec3(在 Contracts)  Quat.cs  CombatWorld.cs  CombatShip.cs  Projectile.cs  Obstacle.cs  CombatRuntime.cs
  Systems/
    MovementSystem.cs  PowerSystem.cs  TargetingSystem.cs  WeaponSystem.cs
    ProjectileSystem.cs  DamageSystem.cs  ObjectiveSystem.cs  SimpleEnemyController.cs
```

### 1. 空间与命令契约

- 坐标按右手系理解：X 右、Y 上、Z 前；舰体局部 +Z 为舰首。单位：km、km/s、km/s²、度、度/秒。
- `SpawnPoint(x,y,z,yaw,pitch,roll)`，保留 3 参构造（等价于 z=pitch=roll=0），旧调用点不用改。
- `HelmCommand { Thrust, Yaw, Pitch, Roll, FlightAssist }`：`Thrust` 是主推力归一化输入（档位值，-0.5 反推），
  三轴是**角加速度输入**而不是转速；`FlightAssist` 是 sticky 开关，仿真记住最近一次值。
- `PowerAllocationCommand { ShipId, Propulsion, Shield, Weapons, Preset }`：能源是每舰一份，必须指定舰船。
- `LockTargetCommand { ShipId, TargetId }`：锁定目标，`TargetId` 为空即解除；细节见第 9 节。
- 快照带位置、姿态（Yaw/Pitch/Roll）、速度三分量、三轴角速度、能源比例、锁定目标与解算出的瞄准点。
  姿态内部用四元数，导出时才转欧拉角，约定与 Unity 的 `Quaternion.Euler(pitch, yaw, roll)` 一致。

### 2. 机动参数全部来自舰体配置

`content/ships/*.json` 的 `combat` 段新增 `hullRadiusKm`、`maneuver`（推力加速度 + 三轴转速上限 + 三轴角加速度）、
`maxSpreadDeg`。契约里对应 `ResolvedShipStats.HullRadiusKm` / `Maneuver` / `MaxSpreadDeg`（`ManeuverProfile` 七字段）。

代码里不再出现「0.6 倍、1.8 倍、4 倍、8 秒」这类倍率——它们只保留在 `ManeuverProfile.Derive()` 一处，
作为**配置缺失时的缺省**（供假实现与测试）。调手感改 JSON，不重编译。

三舰初值：巡逻艇 0.20 km / 4.0 km/s² / 45·27·81 °/s / 180·108·324 °/s²；驱逐舰 0.27 / 3.5 / 32·19·58 / 128·76·230；
巡洋舰 0.37 / 2.5 / 20·12·36 / 80·48·144。

`CampaignFacade.ResolveStats()` 里仍按「旗舰/非旗舰」写死，只是把同一组数值同步进去；
B 的配置加载器落地后整段删除。

### 3. 运动：无阻力 + 档位推力 + 反喷回正

- 推力沿舰首：`v += forward × MaxThrust × ThrustSetpoint × dt`，没有阻力项，松档位靠惯性滑行。
  `forward` 是姿态四元数旋转局部 `(0,0,1)` 的结果，所以推力**恒沿舰体主轴**——侧滑、倒飞、被撞歪之后
  都照样沿舰首加速，这是「无阻力真空 + 主推进」的必然结果，不是简化。姿态与速度完全解耦：
  转向只改下一 tick 的推力方向，不改当前速度。
- 战术航速软上限 `MaxSpeedKps`（游戏性让步，不是物理）：已到顶且推力还在往同向推时**不再施力**
  （避免「满档推不动」），超限部分按比例削回——只改速度大小、不改方向。
- 档位控的是**加速度**而不是航速，所以 HUD 必须同时显示档位与当前加速度，否则玩家以为档位没生效。
- 回正不是「把 ω 设成 0」，而是飞控给一个反向角加速度，和玩家输入共用同一条积分通道：

```text
u = |input|>0 ? input : (assist ? -ω/(α·dt) : 0)
u = clamp(u, -1, 1)
ω = clamp(ω + u·α·dt, ±MaxRate)
```

`|ω| ≤ α·dt` 时该式恰好把 ω 落到 0 且不越过，最后一 tick 的反喷量按剩余转速等比缩小（收力，不是阶跃）。
用巡逻艇的配置（45 °/s、180 °/s²）算：满转松键后 0.25 s 停住，期间余转 5.6°——这就是「松手还带一下」的手感来源。
死区 0.5 °/s；三轴独立判断，按住偏航时俯仰的残余转速仍要继续反喷。
`FlightAssist=false` 时 `u=0`，角速度永不衰减（纯牛顿），用来演示真空无阻尼。

滚转姿态回平是二级开关、**默认关闭**（太空没有黄道面这个「上」），带 `1-|forward·refUp|` 权重淡出，
避免俯冲到竖直时参考退化导致自己乱滚。

### 4. 武器、散布、碰撞

- 武器参数**按炮座给**（`TurretProfile`）：位置、射界、装填、装药、弹速、射程、通道都是每座一份。
  `ResolvedShipStats` 上的 `GunDamage` / `GunRangeKm` / `ReloadSeconds` 只是对外汇总口径
  （最大单发 / 最大射程 / 最快装填），给 AI 站位与 HUD 用，不参与结算。
- 开火顺序是硬约束：**装填 → 瞄准/射程 → 扣电 → 散布 → 出弹 → 该座重装填**，**每座炮各跑一遍**。
  任何一步失败都「什么都不改」：不扣电、不进装填、不动随机序列。早先的实现先扣电再查射程，
  对射程外连点开火会白扣电却不出弹，玩家很难把「电去哪了」归因到这个点上。
- 装填：`该座 ReloadSeconds × (1.4 - 0.8 × 本舰 WeaponsShare)`，交战档最快。每座独立计时。
- 射界：每座一个「中心角 + 半张角」（± `YawHalfArcDeg`，相对舰首），外加俯仰上下限。
  **出界就是不打**，发 `OutOfArc` 事件；旧的全舰 ±15° 锥 + 钳到锥边已删除——钳边开火等于替玩家扣扳机。
  舰首炮半张角取 140° 就伸不到舰尾，所以「炮弹打不到自己舰体」是几何保证，不靠碰撞过滤。
- 不开火的门槛只有两个：装填没好、目标不在射界。**不建模炮塔转速**：数十门炮各带转速会让
  「为什么打不出去」变成玩家读不出的中间量，认知成本大于操作深度。
- 一次开火命令 = 所有装填完且在射界内的炮座同时开火（一次齐射），从**各自炮座位置**出膛。
- 散布：`(1 - Accuracy01) × MaxSpreadDeg`，绕理想方向做锥形偏移，每次开火固定消费两个随机数。
  上限取 **1.0°**：在 60% 射程上落点散布半径约 1.4 倍舰体半径，命中率落在三到五成。
  早先写的 8° 在 180 km 处横向 25 km，比舰体半径大两个量级，会让整场打不中——已作废。
- 炮弹世界速度 = 舰速 + 炮口方向 × 弹速（追击更快、迎头更慢）。
- 命中用**三维线段 vs 球、按两体相对位移**做扫掠：弹速 220 km/s × 1/30 s = 7.3 km/tick，
  舰体半径只有 0.2–0.37 km，点检测 100% 穿透；舰速 32 km/s 也有 1.07 km/tick，同样漏判。
  先判陨石再判舰船，取最近命中；命中只负责回答「谁打到了谁」，扣多少血一律走 `DamageResolver`（见第 10 节）。

### 5. 提前量必须用相对速度（一个会毁掉整场战斗的坑）

炮弹在世界系的速度含发射舰自己的速度，所以解提前量要用**相对速度**：

```text
rel    = targetPos - shooterPos
relVel = targetVel - shooterVel
解 |rel + relVel·t| = 弹速·t，取最小正根
瞄准点 = shooterPos + rel + relVel·t
```

早先版本用目标**绝对**速度（`targetPos + targetVel·t`），漏掉了自己这 30 km/s：
150 km 上偏差几度、几十公里，实测命中率约 1%，双方打到 300 s 仍分不出胜负。
修正后同一场景 69 s 结束，且「关掉提前量就打不死」——这条现在是可验证的对照实验，不是感觉。

### 6. 执行顺序与确定性

`Step()` 固定顺序：角速度输入归零 → 应用待处理命令 → **敌舰 AI 生成意图** → **能源结算** → **护盾 tick** → 运动 → **锁定解算** → 武器 → 弹道 → 伤害 → 胜负 → 时钟推进。

**AI 必须排在能源之前**（2026-11-26 修）。它写下的 `AngularInput` 与 `ThrustSetpoint` 就是这一 tick 的真实输入；
排在能源之后会出现两处不一致：AI 的三轴操舵在能源眼里恒为零（主动转向不扣电），
推力则要等下一帧才计费。玩家走命令队列、AI 走直接赋值，但两者都必须在能源结算前提出意图，
之后同样受能源约束——否则「同参数同规则」这句话只对玩家成立。

能源排在运动之前的理由：推力与操舵输入已经收齐，先按「这一 tick 打算怎么飞」扣电，
再决定推力是否被能源锁死；这样 `MovementSystem` 读到的档位一定是已经过能源约束的值，物理侧不需要知道能源存在。

护盾 tick 夹在能源与伤害之间：它要剪过载窗口、处理破盾恢复、按 4 s 延迟回充。
放在伤害之前，同一 tick 内就不会出现「先被打再回充」导致玩家看到刚回的那点又没了。

锁定解算紧跟在运动之后：位置与速度是本 tick 最新的，解出的提前量不会比 HUD 慢一帧。

全场一个 `DeterministicRandom`，消费顺序固定（陨石生成 → 每次开火散布）；遍历按 `List` 插入序，
`Dictionary` 只查不遍历；四元数每 tick 归一化一次；炮弹 ID 用递增整数。

### 7. 验证方式

- `tools/CombatSimCli`：无 Unity 跑完一局，启动先打印每舰的 `maneuver`、半径、限速与能源容量（调参唯一可见入口），
  再逐 0.5 秒打印位置/姿态/速度/血盾/能源/锁定，最后输出 `BattleResult`。
  参数 `--seed`、`--no-lead`、`--enemies`、`--seconds`、`--quiet`。
  默认走目标锁定（仿真解提前量），`--no-lead` 改成手动瞄目标当前位置——这是「提前量有效」的对照组。
- `tools/SmokeTests` 现有 33 个场景块。后十三个专测本轮的物理、能源、护盾、配置与炮座：
  能源耗尽锁推进/回充恢复、熄火后持续满推仍能重新点火、推力沿舰体主轴（偏航 90° 后加速度必须落在 +X）、
  锁定解提前量与三类拒绝（超距/友军/空目标）、判面三档、转向损耗开关、电不足削 DR、
  双舰三轮破盾、破盾那一击不穿透、6 s 后只回三成、4 s 延迟回充、
  过载窗口「未过期不重复扣 / 部分过期不退还 / 阈值内不扣」、射程外开火不改任何状态、
  配置解析与内置镜像逐字段一致、越界与负数配置报错。
- 33 块（122 条断言）**已在 2026-11-26 实际执行并通过**，CI 见 `.github/workflows/smoke.yml`。
  此前一直只有编译验证，原因是 Smart App Control 锁了新生成的二进制（见第 6 节的说明）。
- 首次真跑暴露的问题不在断言，在**资产平衡**：三个种子各跑 300 s，**0 击沉**（明细见
  `phase1-tasks.md` 的「首次实测」一节）。下面「六个种子 69–129 s 全胜」那条来自等效二维模型，
  **已被真实引擎推翻**，不要再引用——它建模时没有三面 DR，也没有「交汇窗口只有 3–4 s」这件事。
- 能源曲线同样离线复算（主舰 120 点容量、再生 3.2/s、满推耗电 4.4/s）：

  | 状态 | 首次见底 |
  | --- | --- |
  | 纯满推 | 103 s |
  | 满推 + 三轴满转 | 58 s |
  | 半推巡航 | 不会见底 |

  抗伤耗电另计（1 点被挡掉的伤害 = 0.12 电），见第 10 节；它不进这张表，因为取决于对方打得多狠。

  即：正常交战不会因为能源停摆，只有长时间极限机动或边打边扛才会被逼到熄火——这正是它该有的压力。

### 8. 能源：每舰一份，全舰记账

**为什么要改**：早先 `PowerAllocation` 挂在 `CombatWorld` 上，是全场共用的一条口令；而推进系统根本没读它，
`Power.Propulsion` 是个死参数。现在能源归每艘舰自己（`CombatShip.Power` / `Energy`），
`PowerAllocationCommand` 必须带 `ShipId`，不同舰可以各自处在导航/交战档。

**三路持续耗电**，全部由舰体配置的 `power` 块给出（`content/ships/*.json`）：

```text
eff    = 0.55 + 1.35 × Propulsion                // 分给推进的电越多，每单位推力越省
drain  = PropulsionDrain × |thrust| / eff         // 反推 ×1.4（喷口效率低）
       + TurnDrain × (|yaw|+|pitch|+|roll|)/3 / eff
Energy += (Regen − drain) × dt                    // 夹到 [0, Capacity]
开火另扣 WeaponDrainPerShot，扣不出来就打不出去（事件 PowerLow）
```

护盾的电费**不在这里**：它按「被挡掉的伤害」计，1 点 = 0.12 电，落在 `DamageResolver` 里（第 10 节）。
早先那套「回充时按秒扣电」的做法已删掉——同一件事在两个地方收费会两头都调不准，
而且 `shieldDrainPerSecond` 会变成没人读的死配置，正是本文档反复提醒的那类问题。

**耗尽与恢复**（滞回，避免每 tick 抖一下）：

```text
Energy == 0                 → PropulsionLocked = true，ThrustSetpoint 强制 0，事件 PowerEmpty
Energy ≥ Capacity×Restart   → 解锁，事件 PowerRestored
```

**扣电的不变式：扣了电的推力，一定是这一 tick 真会执行的推力。**（2026-11-26 修）
旧实现先按 `ThrustSetpoint` 计费、最后才因锁定把档位清零：熄火后继续请求满推的舰每 tick 都在为
「不会发生的加速」付电，能源卡在 0 上再也回不到重启阈值——AI 每 tick 重写档位，稳定踩中这个死循环
（实测巡洋舰满推 4.4/s 对再生 3.2/s，一旦熄火就永久熄火）。现在按三步走，顺序不能换：

```text
1  滞回解锁（放在计费之前，本 tick 才用得上推进）
2  定推力：锁定时主推进负载 = 0；未锁定但这一档会把电打穿 → 先锁、再按 0 计费
3  Energy += (Regen − 实际执行的主推进与转向负载) × dt
```

第 2 步的「先锁后计费」是重点：它的意义不是省那 0.1 点电，而是保证「扣电 ⇒ 有加速度」这条因果不被破坏。

**已知边界**：熄火期间转向照旧耗电（RCS 是另一路负载）。三型舰的满转耗电（0.5–0.9/s ÷ 最低 eff 0.55，
即 0.9–1.6/s）都低于再生（3.2–4.2/s），所以一定能重启；若将来某型舰的转向耗电逼近再生速率，
就会「熄火即永锁」——那是配置错误，不是代码问题，调参时要看这两个数。

锁定后即使玩家持续提交满推命令，`ApplyPendingCommands` 写入的档位也会被当 tick 的能源结算清零；
`MovementSystem` 另外再判断一次 `PropulsionLocked`，双保险。滑行期间速度不变（真空无阻力），
所以熄火惩罚的是**机动**而不是「掉速」。

**全舰记账（T06）**：曾经有过「只算主舰」的开关 `SimulatesEnergy`，护航舰与敌舰按满电处理。
已删除——敌舰无限电会让「玩家被压制」的结论带假象，也让饱和战术的收益算错。
现在 `PowerSystem` / `TryConsumeShot` / `ShieldSystem.RegenRate` / `DamageResolver` 都对所有舰一视同仁，
每艘舰开局满电、按自己的份额扣电、耗尽了都会熄火。

各舰数值：

| 舰型 | 容量 | 再生/s | 满推耗电/s | 转向/s | 每发 | 重启阈值 |
| --- | --- | --- | --- | --- | --- | --- |
| 巡逻艇 | 80 | 4.2 | 4.8 | 0.5 | 2.5 | 20% |
| 驱逐舰 | 140 | 3.6 | 4.4 | 0.7 | 4.5 | 20% |
| 巡洋舰 | 120 | 3.2 | 4.4 | 0.9 | 7.0 | 18% |

### 9. 目标锁定

`LockTargetCommand { ShipId, TargetId }`，`TargetId` 留空即解除。锁定后不必再给世界坐标瞄准点：

- 每 tick 由 `TargetingSystem` 用**相对速度**解提前量，写进 `Ship.AimPoint`，快照里带
  `LockedTargetId` / `AimX/Y/Z` / `HasAim`，HUD 直接画准星。
- 锁定距离 = `GunRangeKm × 1.2`。给一点余量，让玩家在进射程前先锁上、看见提前量点在动；再远锁定没有意义。
- 目标死亡、跨阵营、超距 → 自动解锁并发 `LockLost`。不「粘着」一个已经打不到的目标。
- `Submit` 阶段就拒绝非法锁定（`LOCK_OUT_OF_RANGE` / `FRIENDLY_TARGET` / `TARGET_DEAD` / `NO_SUCH_TARGET`），
  不让错误命令进入待处理队列——否则玩家看不到失败原因。
- AI 不走这条通道（它自己算提前量），所以锁定是纯玩家侧的体验改进，不改变敌方行为与已标定的战斗节奏。

### 10. 护盾：常驻减伤 + 过载缓冲（2026-11-26）

设计依据见 `docs/weapon-defense-design.md` 第 5、6 节。这一节的目的是让人能从代码反查出规则，
不是重复设计稿的论证过程。

**为什么不是「护盾池」**：池做对了按朝向分配也能有方向性，但它会退化成第二条血条，
玩家看到的是「护盾 62%」而不是「他右舷是软的」。所以护盾拆成两层：

| 层 | 谁来算 | 会不会掉 |
| --- | --- | --- |
| 减伤 DR（正面 0.85 / 侧面 0.55 / 背面 0.15） | `ShieldSystem.EffectiveDr` | 不掉，是常驻几何状态 |
| 过载缓冲池（300 点） | `ShieldSystem.AccumulateOverload` | 会掉，但只在短窗口超额时掉 |

**唯一入口**：`DamageResolver.ResolveHit(world, target, source, channel, amount, incomingDir)`。
顺序固定为 判面 → 护盾 DR（电不够先削）→ 过载 → 装甲 → 结构。`DamageSystem` 只做扫掠命中，
算出 `incomingDir` 就交给它——命中判定与伤害结算分开，才能在不碰扫掠代码的前提下改防御。

**判面**：`incomingDir` 是「从目标指向威胁来源」的单位向量（弹丸相对速度取反）。
在舰体坐标系里与 `forward` 点乘：`cos ≥ cos45` 正面、`cos > −cos45` 侧面、其余背面。
传反这个符号，正面与背面会互换，而且只有贴近时才看得出来——这是本模块最容易犯、最难发现的错。

**转向损耗**（可开关，`CombatWorld.TurnLossEnabled` 默认开）：

```text
load  = max(|ω_yaw|/YawRate, |ω_pitch|/PitchRate, |ω_roll|/RollRate)   // 0..1
DR_eff = DR_face × (1 − 0.25 × load)
```

满转时掉四分之一，巡洋舰正面 0.85 → 0.6375，正好落到侧舷档位附近。留开关是为了灰盒开/关各跑一次：
不动玩家行为就删掉，不留「设计上有、玩家感觉不到」的规则。

**抗伤要付电**：`应付电 = 伤害 × DR × 0.12`。电不够时**先削 DR**（`dr ×= 可用电/应付电`），
再用削过的 DR 算吸收——顺序反了会算出「付不起的电」这种自相矛盾的结果。
全舰记账后这条对所有舰生效：敌舰与护航舰的盾也会因为没电而变薄，所以「盾硬不硬」也取决于它自己的储能。

**过载算法**（口径 = 护盾**实际吸收量**，不是原始伤害；背面挨打时盾没接住什么，就不该过载）：

```text
onHit(absorbed):
  window.push(now, absorbed)
  excess = max(0, sum(window) − threshold)     // threshold 120，window 1.5 s
  delta  = excess − deducted                   // 只扣新增的那部分
  if delta > 0: buffer −= delta; deducted += delta
  if buffer <= 0: buffer = 0; brokenUntil = now + 6

onTick:
  丢掉超过 1.5 s 的条目
  deducted = min(deducted, max(0, sum − threshold))   // 窗口回退时只压上限，不退还
```

`deducted` 唯一的作用是防重复扣：第一笔还没过期时来第二笔，`sum` 与 `excess` 会一起涨，
不减它缓冲池会以两倍速度漏。

算例（池 300、阈值 120，每轮两发 130、轮间隔 2 s）：160 → 20 → 破。
v0.3 写的「单舰永远碰不到过载」「双舰两轮必破」都是错的，已按实际重算。

**破盾状态机**（四个边界都写死，不留含糊）：

| 边界 | 定值 |
| --- | --- |
| 打空池的那一击是否穿透 | **否**，本击仍按已算 DR 结算，失效从下一击开始 |
| 失效 6 s 内能否回充 | **不能**，buffer 锁 0，窗口停止累计 |
| 6 s 后恢复多少 | **30%**（90 点），不是满——回满等于破盾白打 |
| 「4 s 未受击回充」恢复什么 | **缓冲池**。DR 是常驻的，不需要恢复 |

**延迟回充**：`now − LastHitTime ≥ 4 s` 才启动，速率 `RegenPerSec × Shield份额×3 × 供电系数`，
能量低于容量 5% 时线性衰减到 0。这条是破防窗口能存在的原因，破了立刻回等于没破。

**配置位置**：`content/ships/*.json` 的 `shield` / `overload` / `armor` 块。
`armor` 的三个死字段已改名并接上：`coreMultiplier` → `kineticDr`、`outerMultiplier` → `beamDr`，
语义从「乘数」翻成「减伤比例」（实际伤害 = 伤害 × (1 − DR)）。
数值只有一个真源（JSON），`CampaignFacade` 与 CLI 都经 `ShipCatalog` 读取，见第 11 节。

**当前数值后果（必须先知道，否则会以为是 bug）**：现在只有一门动能炮，
正面穿透只剩 `(1−0.85)×(1−0.35) = 0.0975`，即正面 TTK 被拉长到数百秒，
比设计稿按「光束 + 投射体」双通道估的 257 s 还慢。这不是护盾写错，是 T09 还没做。
在双通道落地前，别拿 CLI 的击杀时间去对标单局 90–150 s 的目标。

### 11. 舰船配置加载：JSON 是唯一真源（2026-11-26，T01b）

**问题**：`content/ships/*.json` 早就写好了 `maneuver` / `power` / `shield` / `overload`，
但运行时读的是 `CampaignFacade.ResolveStats()` 与 `tools/CombatSimCli` 里的两份手抄副本。
结果是「数值放在配置里」这句话只对了一半：调参改 JSON 不生效，改代码则没人知道 JSON 是不是过期了。

**落地形态**（`game/Packages/com.starpawn.combat/Runtime/Content/`，独立程序集 `StarPawn.Combat.Content`）：

| 文件 | 干什么 |
| --- | --- |
| `MiniJson.cs` | 最小 JSON 解析（对象/数组/数字/字符串/布尔/null），纯 BCL，不引包 |
| `ShipConfigParser.cs` | 一份 JSON → `ResolvedShipStats`，缺字段报错并给出 `combat.shield.frontDr` 这样的路径 |
| `ShipCatalog.cs` | 扫目录、按 `id` 建索引，实现 `IShipStatsSource` |

**分层**：`IShipStatsSource` 在 Combat.Contracts；应用层只认接口（`GameServices.Ships`），
Bootstrap 决定装 `ShipCatalog.LoadDirectory(contentRoot/ships)`。CLI 自己也用同一个加载器，
默认从 cwd 向上找 `content/ships`，可用 `--content` 覆盖。

**缺字段必须报错，不许静默补缺省**：静默补缺省等于在代码里又藏一份数值表，
调参时改 JSON 不生效而没人知道为什么。这条是 T01b 的全部意义。

**先校验原始值，再构造对象**（2026-11-26 修）。契约层的构造函数会「修正」非法值——
`frontDr: 1.5` 截成 1、`capacity: -5` 改成 1、`windowSeconds: -1` 改成 1.5 秒。
于是配置能「解析成功」，跑起来的却不是作者填的数，而且只在战斗里表现为「这舰怪怪的」。
现在每个取值方法本身就是校验：有限数（拒绝 NaN/Infinity）+ 区间（`Unit` = [0,1]、`Positive`、`NonNeg`、
`OpenUnit` = (0,1]），越界直接记错、不截断。`1.5` 这种典型错误会报
`combat.shield.frontDr 必须在 [0,1]（是减伤比例，不是倍数），当前 1.5`。
构造之后的检查只剩跨字段一致性（如缓冲池不得小于过载阈值）。

**装配期不吞错误**（2026-11-26 修）。`GameBootstrap.Create(contentRoot)` 有两种语义，不能混：

| 传入 | 行为 |
| --- | --- |
| `contentRoot = null` | 明确选择不读配置，用内置镜像（编辑器里还没有内容、纯演示） |
| `contentRoot = 路径` | 必须读到且必须全对；读不到、解析出错、一个舰都没有 → 抛 `InvalidOperationException` 并列出全部错误 |

早先的实现把 `LoadDirectory(..., out _)` 的错误丢掉、失败时悄悄退回内置表：调参者改了 JSON、
战斗里还是旧数值，没有任何一处会说「你的配置没生效」。崩溃比这个好查。

**为什么不用 `System.Text.Json`**：Domain 与 Content 都是 `noEngineReferences`，
Unity 里能不能用取决于 API Compatibility Level 与是否额外塞了 DLL；加载器不该被这件事绑住。
（Infrastructure 的 `ContentRepository` 用的是 `System.Text.Json`，那是 B 的领地，未改动。）

**`ShipCatalog.Builtin` 是兜底，不是第二真源**：只服务于「显式不传路径」的测试与演示，
数值必须与 JSON 完全一致。`SmokeTests` 第 28 项逐字段比对磁盘配置与 Builtin，漂移会直接失败；
完整加载器（B 的完整版，含 mod 与热重载）接入后这个属性应整体删除。

**已知边界**：本条只做「JSON → 运行时属性」的通路，path 由调用方给。
Unity 打包后 content 在哪（StreamingAssets / Addressables）仍是 B 的装配问题，不在本任务范围内。
