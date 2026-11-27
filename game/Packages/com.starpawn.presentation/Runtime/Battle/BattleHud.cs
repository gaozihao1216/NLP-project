// 灰盒 HUD：OnGUI 直接画，不依赖 UI 资产（uGUI Canvas 以后再换）。
//
// 内容分五块：
//   左下  玩家舰状态（血量 / 护盾缓冲 / 能源 / 装填 / 炮塔 / 能量档 / 推力档 / 转速锁警告）
//   右上  键位说明
//   右下  敌我统计 + 最近被拒命令
//   世界标  提前量准星与锁定目标框（世界坐标投影到屏幕）
//   中上  胜负与结算信息

using UnityEngine;
using StarPawn.Combat.Contracts;

namespace StarPawn.Presentation
{
    public class BattleHud : MonoBehaviour
    {
        public BattleSceneController Controller;
        public BattleViewRegistry Registry;
        public BattleInputController Input;

        private BattleSnapshot _snap;

        private void Update()
        {
            _snap = Controller != null ? Controller.Snapshot : null;
        }

        private void OnGUI()
        {
            ShipSnapshot player = FindPlayer(_snap);
            DrawStatusPanel(player);
            DrawHelp();
            DrawSummary();
            DrawWorldMarkers(player);

            if (Controller != null && Controller.ResultShown)
            {
                DrawResultBanner();
            }
        }

        // ---- 左下：玩家舰状态 ----

        private void DrawStatusPanel(ShipSnapshot player)
        {
            if (player == null)
            {
                GUI.Box(new Rect(10, Screen.height - 130, 360, 120), "旗舰阵亡 / 无玩家舰");
                return;
            }

            float x = 10f;
            float y = Screen.height - 240f;
            float w = 380f;
            GUI.Box(new Rect(x - 4f, y - 8f, w + 8f, 236f), "旗舰 " + player.ShipId);

            Bar(x, y, w, "舰体", player.Health01, new Color(0.25f, 0.85f, 0.35f));
            y += 26f;
            Bar(x, y, w, player.ShieldBroken ? "护盾(破!)" : "护盾缓冲", player.Buffer01,
                player.ShieldBroken ? new Color(0.9f, 0.2f, 0.15f) : new Color(0.3f, 0.65f, 1f));
            y += 26f;
            Bar(x, y, w, "能源" + (player.PropulsionLocked ? "(推进锁!)" : ""), player.Energy01,
                player.PropulsionLocked ? new Color(1f, 0.5f, 0.1f) : new Color(0.95f, 0.85f, 0.25f));
            y += 26f;
            Bar(x, y, w, "装填", player.Reload01, new Color(0.7f, 0.7f, 0.75f));
            y += 28f;

            GUI.Label(new Rect(x, y, w, 22f),
                "炮塔 " + player.TurretsLoaded + "/" + player.TurretCount + " 在界 " + player.TurretsInArc +
                "   能量档 " + player.PowerMode + (Input != null ? "  (1/2/3 切换)" : ""));
            y += 22f;
            GUI.Label(new Rect(x, y, w, 22f),
                "推力档 " + (Input != null ? Input.ThrustSetpoint.ToString("0.00") : "-") +
                "   速度 " + player.Speed.ToString("0.0") + " km/s" +
                "   飞控 " + (Input != null && Input.FlightAssist ? "开(F)" : "关(F)"));
            y += 22f;
            string aimMode = player.HasAim
                ? (!player.LockedTargetId.IsEmpty
                    ? "锁定 " + player.LockedTargetId + "（提前量已解算）"
                    : (Input != null && Input.ManualAimActive ? "手动瞄准（鼠标）" : "无瞄准点"))
                : "无瞄准点（移动准星或 Tab 锁定）";
            GUI.Label(new Rect(x, y, w, 22f), "瞄准 " + aimMode);
            y += 22f;

            // 三面 DR：亮度随数值变化，"右舷是软的"一眼能读出来。
            GUI.color = new Color(1f, 1f, 1f, 0.3f + 0.7f * player.FaceDrFront);
            GUI.Label(new Rect(x, y, 120f, 22f), "正面DR " + player.FaceDrFront.ToString("0.00"));
            GUI.color = new Color(1f, 1f, 1f, 0.3f + 0.7f * player.FaceDrSide);
            GUI.Label(new Rect(x + 125f, y, 120f, 22f), "侧舷DR " + player.FaceDrSide.ToString("0.00"));
            GUI.color = new Color(1f, 1f, 1f, 0.3f + 0.7f * player.FaceDrRear);
            GUI.Label(new Rect(x + 250f, y, 120f, 22f), "背面DR " + player.FaceDrRear.ToString("0.00"));
            GUI.color = Color.white;
        }

        private static void Bar(float x, float y, float w, string label, float v, Color c)
        {
            v = Mathf.Clamp01(v);
            GUI.Label(new Rect(x, y, 90f, 20f), label);
            GUI.color = new Color(0.15f, 0.15f, 0.18f);
            GUI.Box(new Rect(x + 92f, y + 3f, w - 92f, 14f), "");
            GUI.color = c;
            GUI.Box(new Rect(x + 92f, y + 3f, (w - 92f) * v, 14f), "");
            GUI.color = Color.white;
        }

        // ---- 右上：键位说明 ----

        private static void DrawHelp()
        {
            // 键位以 docs/combat-controls.md §0/§2 为准（2026-11-27 对齐文档版）。
            string help =
                "核心  W/S 俯仰  A/D 偏航  空格 推力档\n" +
                "常用  左键 开火(按住)  Tab 锁定/解除\n" +
                "辅助  Q/E 滚转  F 飞控  1/2/3 能源档\n" +
                "高级  Shift 反推(按住)\n" +
                "鼠标 瞄准  滚轮 变焦  右键拖 视角\n" +
                "Enter(结束后) 重新开局";
            GUI.Box(new Rect(Screen.width - 280f, 10f, 270f, 116f), help);
        }

        // ---- 右下：战况统计 ----

        private void DrawSummary()
        {
            if (_snap == null || _snap.Ships == null) return;

            int enemiesAlive = 0, alliesAlive = 0;
            for (int i = 0; i < _snap.Ships.Length; i++)
            {
                ShipSnapshot s = _snap.Ships[i];
                if (!s.IsAlive) continue;
                if (s.IsAlly) alliesAlive++;
                else enemiesAlive++;
            }

            string text = "我方存活 " + alliesAlive + "   敌舰存活 " + enemiesAlive +
                "\n战斗时间 " + _snap.Clock.Seconds.ToString("0.0") + " s";
            if (Input != null && !string.IsNullOrEmpty(Input.LastCommandError))
            {
                text += "\n最近拒绝: " + Input.LastCommandError;
            }
            GUI.Box(new Rect(Screen.width - 280f, Screen.height - 92f, 270f, 82f), text);
        }

        // ---- 世界坐标标记 ----

        private void DrawWorldMarkers(ShipSnapshot player)
        {
            if (player == null || Controller == null || Controller.MainCamera == null) return;

            Camera cam = Controller.MainCamera;

            // 提前量准星（仿真解算的 AimPoint）。
            if (player.HasAim)
            {
                Vector3 screen = cam.WorldToScreenPoint(SimMath.ToWorld(player.AimX, player.AimY, player.AimZ));
                if (screen.z > 0f)
                {
                    DrawCross(screen.x, Screen.height - screen.y, 16f, new Color(1f, 0.85f, 0.3f), "提前量");
                }
            }

            // 锁定目标框 + 距离。
            if (!player.LockedTargetId.IsEmpty && Registry != null)
            {
                ShipSnapshot target = Registry.TryGetShipSnapshot(player.LockedTargetId);
                if (target != null && target.IsAlive)
                {
                    Vector3 screen = cam.WorldToScreenPoint(SimMath.ToWorld(target.X, target.Y, target.Z));
                    if (screen.z > 0f)
                    {
                        float dx = target.X - player.X;
                        float dy = target.Y - player.Y;
                        float dz = target.Z - player.Z;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                        DrawCross(screen.x, Screen.height - screen.y, 26f, new Color(1f, 0.25f, 0.2f),
                            target.ShipId + " " + dist.ToString("0") + " km");
                    }
                }
            }
        }

        private static void DrawCross(float sx, float sy, float half, Color c, string label)
        {
            GUI.color = c;
            GUI.Box(new Rect(sx - half, sy - 1f, half * 2f, 2f), "");
            GUI.Box(new Rect(sx - 1f, sy - half, 2f, half * 2f), "");
            if (!string.IsNullOrEmpty(label))
            {
                GUI.Label(new Rect(sx + half + 4f, sy - 10f, 180f, 20f), label);
            }
            GUI.color = Color.white;
        }

        // ---- 中上：胜负横幅 ----

        private void DrawResultBanner()
        {
            BattleResult r = Controller.Result;
            string title = r != null && r.Victory ? "胜  利" : "失  败";
            GUI.color = r != null && r.Victory ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.35f, 0.3f);
            GUI.Label(new Rect(Screen.width * 0.5f - 120f, Screen.height * 0.25f, 240f, 60f), title);
            GUI.color = Color.white;
            string msg = Controller.SettleMessage ?? "";
            GUI.Label(new Rect(Screen.width * 0.5f - 180f, Screen.height * 0.25f + 46f, 360f, 24f),
                msg + "   按 Enter 重新开局");
        }

        private static ShipSnapshot FindPlayer(BattleSnapshot snap)
        {
            if (snap == null || snap.Ships == null) return null;
            for (int i = 0; i < snap.Ships.Length; i++)
            {
                ShipSnapshot s = snap.Ships[i];
                if (s.IsAlly && s.Role == SquadRole.Flagship && s.IsAlive) return s;
            }
            return null;
        }
    }
}
