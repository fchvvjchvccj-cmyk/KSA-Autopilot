using System;
using System.IO;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.GlfwApi;
using Brutal.Numerics;
using KSA;

namespace KsaAutopilot
{
    public static class Autopilot
    {
        private static readonly object Gate = new object();
        private sealed class Binding { public Vehicle Owner; public bool Ended; }
        private static readonly ConditionalWeakTable<FlightComputer,Binding> Owners = new ConditionalWeakTable<FlightComputer,Binding>();
        private static Vehicle owner;
        private static readonly Guidance Pilot = new Guidance();
        private static bool visible = true;
        private static float targetKm = 200, heading = 90, turnStartKm = 1.5f, turnEndKm = 65;
        private static string notice = "准备好火箭后，点击自动起飞入轨。";
        private static double lastTime = double.NaN;
        private static double3 desired;
        private static bool hasDirection;
        private static float telemetryThrottle;
        private static int lastDrawFrame = -1;
        public static void AfterControl(FlightComputer fc, ref FlightComputerOutput output)
        {
            lock (Gate)
                if (Pilot.Active && Owners.TryGetValue(fc,out Binding b) && ReferenceEquals(b.Owner,owner))
                    // 滑行时也要唤醒导航，不能沿用无控制输入时的无限休眠。
                    output.NextWakeupDeltaTime = Math.Min(output.NextWakeupDeltaTime,0.1);
        }

        private static Vec V(double3 x) => new Vec(x.X,x.Y,x.Z);
        private static double3 B(Vec x) => new double3(x.X,x.Y,x.Z);
        private static void Log(string s)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"autopilot.log"),DateTime.UtcNow.ToString("O")+" "+s+Environment.NewLine); } catch { }
        }
        public static void BeforePrepare(Vehicle v, ref ManualControlInputs input)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(owner,v) || !Pilot.Active) return;
                // 主线程的手动油门始终关机，结束接管后不会遗留点火命令。
                input.EngineOn = false; input.EngineThrottle = 0;
                if (!ReferenceEquals(KSA.Program.ControlledVehicle,v) || v.IsDisposed)
                    Stop("切换飞行器或飞行器已移除，自动驾驶中止");
                else if (Universe.SimulationSpeed > 1)
                    Stop("时间加速已开启，自动驾驶中止；请使用 1 倍速");
            }
        }
        public static void Bind(VehicleUpdateState state)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(state.ReadOnlyVehicle,owner) || !Pilot.Active) return;
                FlightComputer fc = state.FlightComputer;
                Owners.Remove(fc);
                Owners.Add(fc,new Binding { Owner = owner });
            }
        }
        public static void PlayerInput(Vehicle v, InputAction action)
        {
            lock (Gate)
                if (ReferenceEquals(v,owner) && Pilot.Active &&
                    (action == InputAction.MainEngineShutdown || action == InputAction.Forward ||
                     action == InputAction.Backward || action == InputAction.Left || action == InputAction.Right ||
                     action == InputAction.RollLeft || action == InputAction.RollRight))
                    Stop("手动控制已接管，自动驾驶中止");
        }
        private static void Stop(string reason)
        {
            Pilot.Abort(reason); hasDirection = false; telemetryThrottle = 0; notice = reason;
            Log(reason);
        }
        public static void Control(FlightComputer fc, ref FlightComputerNavigation nav, ref ManualControlInputs input)
        {
            lock (Gate)
            {
                if (!Owners.TryGetValue(fc,out Binding b) || !ReferenceEquals(b.Owner,owner) || b.Ended) return;
                try
                {
                    if (!Pilot.Active)
                    {
                        input.EngineOn = false; input.EngineThrottle = 0;
                        fc.BurnMode = FlightComputerBurnMode.Manual;
                        fc.SetInertialNullRot(); fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
                        b.Ended = true; return;
                    }
                    double now = nav.Time.Seconds();
                    double dt = double.IsNaN(lastTime) ? 0 : now-lastTime;
                    lastTime = now;
                    double thrust = fc.ActiveEnginePerformanceMax.Thrust;
                    double mass = Math.Max(0.001,fc.TotalMassPropsBody.Mass);
                    Phase previous = Pilot.Phase;
                    Pilot.Step(V(nav.PositionCci),V(nav.VelocityCci),nav.Parent.Mu,nav.Parent.MeanRadius,thrust/mass,dt);
                    if (previous != Pilot.Phase) Log(Pilot.Message);
                    fc.BurnMode = FlightComputerBurnMode.Manual;
                    fc.ManualThrustMode = FlightComputerManualThrustMode.Direct;
                    fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
                    fc.RollMode = FlightComputerRollMode.Decoupled;
                    fc.RCSMode = FlightComputerRCSMode.Enabled;
                    fc.AttitudeFrame = VehicleReferenceFrame.EclBody;
                    fc.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
                    desired = B(Pilot.Direction); hasDirection = Pilot.Direction.Finite && Pilot.Direction.Length > 0.5;
                    double3 current = Double3Ex.Transform(double3.UnitX,doubleQuat.Concatenate(fc.Ctrl2Asmb,nav.Body2Cci));
                    double alignment = double3.Dot(current,desired);
                    // 姿态偏差超过 20 度时先转向，避免横向点火；起飞前已要求火箭直立。
                    telemetryThrottle = (float)(Pilot.Active && alignment > Math.Cos(20*Math.PI/180) ? Pilot.Throttle : 0);
                    input.EngineOn = telemetryThrottle > 0; input.EngineThrottle = telemetryThrottle;
                    input.ThrusterCommandFlags = default;
                    if (!Pilot.Active)
                    {
                        input.EngineOn = false; input.EngineThrottle = 0; hasDirection = false;
                        fc.SetInertialNullRot(); fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
                        b.Ended = true; notice = Pilot.Message;
                    }
                }
                catch (Exception ex)
                {
                    Stop("控制异常，已中止："+ex.GetType().Name);
                    input.EngineOn = false; input.EngineThrottle = 0;
                    fc.BurnMode = FlightComputerBurnMode.Manual; b.Ended = true;
                    Log(ex.ToString());
                }
            }
        }
        public static void Attitude(FlightComputer fc, ref FlightComputerNavigation nav)
        {
            lock (Gate)
            {
                if (!Pilot.Active || !hasDirection || !Owners.TryGetValue(fc,out Binding b) || !ReferenceEquals(b.Owner,owner)) return;
                // 游戏控制轴为 +X；以轨道法向固定滚转，避免竖直起飞时坐标退化。
                double3 side = double3.Cross(desired,nav.PositionCci);
                if (side.Length() < 1e-6) side = double3.Cross(desired,double3.UnitZ);
                if (side.Length() < 1e-6) side = double3.Cross(desired,double3.UnitY);
                side = double3.Normalize(side);
                double3 down = double3.Normalize(double3.Cross(desired,side));
                doubleQuat q = doubleQuat.CreateFromRotationMatrix(new double4x4(
                    desired.X,desired.Y,desired.Z,0,side.X,side.Y,side.Z,0,down.X,down.Y,down.Z,0,0,0,0,1));
                fc.AttitudeTarget = new AttitudeTarget { Target2Cci = q, RatesCci = double3.Zero };
            }
        }
        public static void Draw()
        {
            try { DrawCore(); }
            catch (Exception ex) { lock (Gate) { if (Pilot.Active) Stop("界面异常，已停止自动驾驶"); } Log(ex.ToString()); }
        }
        private static void DrawCore()
        {
            lock (Gate)
            {
                int frame = ImGui.GetFrameCount();
                if (frame == lastDrawFrame) return;
                lastDrawFrame = frame;
                if (ImGui.IsKeyPressed(ImGuiKey.F8,false)) visible = !visible;
                if (Pilot.Active && ImGui.IsKeyPressed(ImGuiKey.Escape,false)) Stop("已按 Esc 中止");
                if (Pilot.Active && !ReferenceEquals(KSA.Program.ControlledVehicle,owner)) Stop("已切换飞行器");
                if (!visible) return;
                if (Universe.CurrentSystem == null) return;
                float2 size = new float2(620,480);
                ImGui.SetNextWindowSize(in size,ImGuiCond.FirstUseEver);
                bool expanded = ImGui.Begin("自动驾驶 · 起飞入轨###KsaAutopilot",ImGuiWindowFlags.None);
                try
                {
                    if (!expanded) return;
                    ImGui.TextWrapped("F8 显示/隐藏 · Esc 或手动姿态键中止");
                    ImGui.TextWrapped("状态："+Pilot.Message);
                    if (!Pilot.Active)
                    {
                        ImGui.InputFloat("目标轨道高度 (km)###ap-alt",ref targetKm,10,100,"%.1f");
                        ImGui.SliderFloat("发射方位角###ap-heading",ref heading,0,359,"%.0f 度");
                        ImGui.InputFloat("开始转弯高度 (km)###ap-turn-start",ref turnStartKm,0.1f,1,"%.1f");
                        ImGui.InputFloat("结束转弯高度 (km)###ap-turn-end",ref turnEndKm,5,10,"%.1f");
                        ImGui.Text("方位角：0 北 / 90 东 / 180 南 / 270 西");
                        if (ImGui.Button("自动起飞入轨###ap-start")) Start();
                    }
                    else if (ImGui.Button("中止并关闭发动机###ap-stop")) Stop("用户中止");
                    ImGui.TextWrapped(notice);
                    if (owner != null)
                    {
                        ImGui.Text($"高度 {Pilot.Altitude/1000:F1} km · 垂直速度 {Pilot.VerticalSpeed:F0} m/s");
                        ImGui.Text($"远地点 {Pilot.Apoapsis/1000:F1} km · 近地点 {Pilot.Periapsis/1000:F1} km");
                        ImGui.Text($"目标仰角 {Pilot.PitchDegrees:F1} 度 · 油门 {telemetryThrottle*100:F0}%");
                    }
                    ImGui.TextWrapped("需足够推重比、燃料和姿态控制能力，使用 1 倍速。");
                    ImGui.TextWrapped("先启用所需发动机；多级火箭需要手动分级。");
                }
                finally { ImGui.End(); }
            }
        }
        private static void Start()
        {
            Vehicle v = KSA.Program.ControlledVehicle;
            if (v == null || v.IsDisposed || v is KittenEva || v.IsDebris) { notice = "请先控制一枚火箭。"; return; }
            if (Universe.SimulationSpeed != 1) { notice = "请将模拟速度设为 1 倍速。"; return; }
            if (v.FlightComputer.BurnMode == FlightComputerBurnMode.Auto) { notice = "请先关闭原生自动机动。"; return; }
            double radius = v.Parent.MeanRadius, r = v.GetPositionCci().Length();
            if (r-radius > Math.Max(10000,targetKm*100)) { notice = "请在地面或起飞初段启动自动入轨。"; return; }
            double acceleration = v.ComputeActiveThrust(1)/Math.Max(0.001,v.TotalMass);
            if (!double.IsFinite(acceleration) || acceleration < v.Parent.Mu/(r*r)*1.1) { notice = "可用推重比不足 1.1：请检查发动机是否启用。"; return; }
            double3 up = double3.Normalize(v.GetPositionCci());
            double3 axis = Double3Ex.Transform(double3.UnitX,doubleQuat.Concatenate(v.FlightComputer.Ctrl2Asmb,v.GetBody2Cci()));
            if (double3.Dot(axis,up) < Math.Cos(20*Math.PI/180)) { notice = "请让火箭竖直朝上，偏差需小于 20 度。"; return; }
            Pilot.TargetAltitude = targetKm*1000; Pilot.HeadingDegrees = heading;
            Pilot.TurnStart = turnStartKm*1000; Pilot.TurnEnd = turnEndKm*1000;
            v.ProcessInput(InputAction.MainEngineShutdown,GlfwKeyAction.Press,0);
            try { Pilot.Start(); } catch (ArgumentException ex) { notice = ex.Message; return; }
            owner = v; lastTime = double.NaN; hasDirection = false;
            notice = "自动驾驶已接管；请保持游戏运行。";
            Log($"开始入轨 altitude={Pilot.TargetAltitude} heading={heading}");
        }
    }
}
