using Godot;
using Nebula;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Heavenlode
{
    /// <summary>
    /// A network transform component optimized for orbital bodies.
    /// Instead of streaming position updates, it syncs orbital parameters once
    /// and calculates positions deterministically on both client and server.
    /// 
    /// This approach:
    /// - Uses minimal bandwidth (parameters sent once, not every tick)
    /// - Provides perfectly smooth motion at any orbital speed
    /// - Works via client-side prediction based on shared game tick
    /// </summary>
    public partial class OrbitalNetTransform : NetNode3D
    {
        [Export]
        public Node3D TargetNode { get; set; }

        /// <summary>
        /// The parent orbital body to orbit around. Null for stars/root bodies.
        /// </summary>
        [Export, NetProperty]
        public OrbitalNetTransform OrbitalParent { get; set; }

        /// <summary>
        /// Distance from the orbital parent center.
        /// </summary>
        [Export, NetProperty]
        public float OrbitalRadius { get; set; }

        /// <summary>
        /// The gravitational mass parameter (GM) of this body, for calculating orbital periods of children.
        /// </summary>
        [Export, NetProperty]
        public float GravitationalMass { get; set; }

        /// <summary>
        /// The orbital period in seconds (time to complete one orbit).
        /// Calculated from parent's gravitational mass and orbital radius.
        /// </summary>
        [Export, NetProperty]
        public float OrbitalPeriod { get; set; }

        /// <summary>
        /// The orbital angle (in radians) at the start of the orbit (tick 0).
        /// </summary>
        [Export, NetProperty]
        public float StartingAngle { get; set; }

        /// <summary>
        /// The rotational period (day length) in seconds. 0 = no rotation.
        /// </summary>
        [Export, NetProperty]
        public float RotationPeriod { get; set; }

        /// <summary>
        /// The Y position (constant for orbital plane).
        /// </summary>
        [Export, NetProperty]
        public float OrbitalPlaneY { get; set; }

        /// <summary>
        /// The tick at which this orbital body was initialized.
        /// Used as the reference point for calculating elapsed time.
        /// </summary>
        [Export, NetProperty]
        public int StartingTick { get; set; }

        /// <summary>
        /// Tracks whether Initialize() has been called on the server.
        /// On clients, we check if OrbitalPeriod > 0 or GravitationalMass > 0 as a proxy for initialization.
        /// </summary>
        private bool _isInitialized = false;
        private bool _hasLoggedProcess = false;

        /// <summary>
        /// Continuous elapsed time in seconds since StartingTick.
        /// Updated every frame for smooth motion.
        /// </summary>
        private double _elapsedTime = 0.0;

        /// <summary>
        /// Whether we've initialized our elapsed time from the network tick.
        /// </summary>
        private bool _timeInitialized = false;

        /// <summary>
        /// Returns true if this orbital body has been initialized (server) or has received network data (client).
        /// For root bodies (stars): GravitationalMass > 0
        /// For orbiting bodies (planets): OrbitalPeriod > 0
        /// </summary>
        public bool IsReady => _isInitialized || GravitationalMass > 0 || OrbitalPeriod > 0;

        public override void _Ready()
        {
            if (Engine.IsEditorHint()) return;
            base._Ready();
            TargetNode ??= GetParent<Node3D>();
        }

        public override void _WorldReady()
        {
            base._WorldReady();
            TargetNode ??= GetParent<Node3D>();
        }

        /// <summary>
        /// Initialize the orbital parameters. Called on the server when setting up the solar system.
        /// </summary>
        public void Initialize(float gravitationalMass, float surfaceGravity, float surfaceRadius, OrbitalNetTransform parent, float radius, float startAngle, float dayLength, bool tidallyLocked)
        {
            GravitationalMass = gravitationalMass > 0 ? gravitationalMass : surfaceGravity * surfaceRadius * surfaceRadius;
            OrbitalParent = parent;
            OrbitalRadius = radius;
            StartingAngle = startAngle;
            OrbitalPlaneY = parent?.OrbitalPlaneY ?? (TargetNode?.GlobalPosition.Y ?? 0f);

            if (parent != null && radius > 0)
            {
                // Calculate orbital period using Kepler's third law: T = 2π * sqrt(r³/GM)
                OrbitalPeriod = 2f * Mathf.Pi * Mathf.Sqrt(Mathf.Pow(radius, 3) / parent.GravitationalMass);

                if (tidallyLocked)
                {
                    RotationPeriod = OrbitalPeriod;
                }
                else
                {
                    RotationPeriod = dayLength;
                }
            }
            else
            {
                OrbitalPeriod = 0;
                RotationPeriod = dayLength;
            }

            _isInitialized = true;
            StartingTick = Network.CurrentWorld.CurrentTick;
        }

        /// <summary>
        /// Get the current orbital angle based on the game tick (converts to elapsed seconds).
        /// </summary>
        public float GetCurrentAngle(int tick) => GetCurrentAngle((double)tick / NetRunner.TPS);

        /// <summary>
        /// Get the current orbital angle based on elapsed time in seconds.
        /// </summary>
        public float GetCurrentAngle(double elapsedSeconds)
        {
            if (OrbitalPeriod <= 0) return StartingAngle;

            // Calculate how far through the orbit we are
            float angularVelocity = 2f * Mathf.Pi / OrbitalPeriod;
            return StartingAngle + (angularVelocity * (float)elapsedSeconds);
        }

        /// <summary>
        /// Get the current rotation angle based on the game tick (converts to elapsed seconds).
        /// </summary>
        public float GetCurrentRotation(int tick) => GetCurrentRotation((double)tick / NetRunner.TPS);

        /// <summary>
        /// Get the current rotation angle based on elapsed time in seconds.
        /// </summary>
        public float GetCurrentRotation(double elapsedSeconds)
        {
            if (RotationPeriod <= 0) return 0;

            float angularVelocity = 2f * Mathf.Pi / RotationPeriod;
            return angularVelocity * (float)elapsedSeconds;
        }

        /// <summary>
        /// Calculate the absolute position at a given tick (converts to elapsed seconds).
        /// </summary>
        public Vector3 CalculatePosition(int tick) => CalculatePosition((double)tick / NetRunner.TPS);

        /// <summary>
        /// Calculate the absolute position at a given elapsed time in seconds.
        /// </summary>
        public Vector3 CalculatePosition(double elapsedSeconds)
        {
            if (OrbitalParent == null || OrbitalRadius <= 0)
            {
                return new Vector3(0, OrbitalPlaneY, 0);
            }

            // Convert our elapsed time to parent's time reference frame
            // Our global tick = StartingTick + (elapsedSeconds * TPS)
            // Parent's elapsed = (our global tick - parent.StartingTick) / TPS
            double tickOffset = (StartingTick - OrbitalParent.StartingTick) / (double)NetRunner.TPS;
            double parentElapsed = elapsedSeconds + tickOffset;

            Vector3 parentPosition = OrbitalParent.CalculatePosition(parentElapsed);

            float currentAngle = GetCurrentAngle(elapsedSeconds);
            Vector3 offset = new Vector3(
                Mathf.Sin(currentAngle) * OrbitalRadius,
                0,
                Mathf.Cos(currentAngle) * OrbitalRadius
            );

            return parentPosition + offset;
        }

        /// <summary>
        /// Calculate the orbital velocity at a given tick (converts to elapsed seconds).
        /// </summary>
        public Vector3 CalculateVelocity(int tick) => CalculateVelocity((double)tick / NetRunner.TPS);
        public Vector3 CalculateVelocity(double elapsedSeconds)
        {
            if (OrbitalParent == null || OrbitalPeriod <= 0 || OrbitalRadius <= 0)
            {
                return Vector3.Zero;
            }

            // Convert to parent's time reference
            double tickOffset = (StartingTick - OrbitalParent.StartingTick) / (double)NetRunner.TPS;
            double parentElapsed = elapsedSeconds + tickOffset;

            Vector3 parentVelocity = OrbitalParent.CalculateVelocity(parentElapsed);

            float currentAngle = GetCurrentAngle(elapsedSeconds);
            float orbitalSpeed = 2f * Mathf.Pi * OrbitalRadius / OrbitalPeriod;

            Vector3 velocity = new Vector3(
                Mathf.Cos(currentAngle) * orbitalSpeed,
                0,
                -Mathf.Sin(currentAngle) * orbitalSpeed
            );

            return parentVelocity + velocity;
        }

        private double _renderTick = -1;
        private bool _renderTickInitialized = false;
        public override void _Process(double delta)
        {
            if (Engine.IsEditorHint()) return;
            if (!Network.IsWorldReady) return;
            base._Process(delta);

            if (!IsReady) return;
            if (OrbitalRadius > 0 && OrbitalParent != null && !OrbitalParent.IsReady) return;
            if (TargetNode == null || !TargetNode.IsInsideTree()) return;

            // Use the SAME adaptive render tick algorithm as NetPropertiesSerializer
            int currentTick = Network.CurrentWorld.CurrentTick;
            int delayTicks = 2; // Must match the buffered interpolation delay
            double targetRenderTick = currentTick - delayTicks;

            if (!_renderTickInitialized)
            {
                _renderTick = targetRenderTick;
                _renderTickInitialized = true;
            }

            // Identical adaptive correction from NetPropertiesSerializer
            double nominalAdvance = delta * NetRunner.TPS;
            double error = targetRenderTick - _renderTick;
            double correction = error * 0.2;
            double advance = nominalAdvance + correction;
            advance = Mathf.Clamp(advance, 0.0, nominalAdvance * 2.0);
            _renderTick += advance;

            // Convert global render tick to elapsed time for THIS body
            double elapsedSeconds = (_renderTick - StartingTick) / (double)NetRunner.TPS;

            Vector3 position = CalculatePosition(elapsedSeconds);
            TargetNode.GlobalPosition = position;

            float rotation = GetCurrentRotation(elapsedSeconds);
            TargetNode.GlobalRotation = new Vector3(0, -rotation, 0);
        }

        /// <summary>
        /// Get position at the current render tick (for external synchronization).
        /// </summary>
        public Vector3 GetPositionAtRenderTick()
        {
            double elapsedSeconds = (_renderTick - StartingTick) / (double)NetRunner.TPS;
            return CalculatePosition(elapsedSeconds);
        }

        /// <summary>
        /// Get the velocity relative to the surface at a given global position.
        /// Accounts for both orbital velocity and rotational velocity.
        /// </summary>
        public Vector3 GetRelativeVelocityToSurface(Vector3 globalPosition, Vector3 objectVelocity, int tick)
            => GetRelativeVelocityToSurface(globalPosition, objectVelocity, (double)tick / NetRunner.TPS);

        /// <summary>
        /// Get the velocity relative to the surface at a given global position using elapsed seconds.
        /// Accounts for both orbital velocity and rotational velocity.
        /// </summary>
        public Vector3 GetRelativeVelocityToSurface(Vector3 globalPosition, Vector3 objectVelocity, double elapsedSeconds)
        {
            // Orbital velocity
            Vector3 orbitalVelocity = CalculateVelocity(elapsedSeconds);

            // Surface rotational velocity
            Vector3 relativePosition = globalPosition - CalculatePosition(elapsedSeconds);
            Vector3 rotationalVelocity = Vector3.Zero;
            if (RotationPeriod > 0)
            {
                float angularSpeed = 2f * Mathf.Pi / RotationPeriod;
                rotationalVelocity = new Vector3(0, -angularSpeed, 0).Cross(relativePosition);
            }

            return objectVelocity - (orbitalVelocity + rotationalVelocity);
        }

        /// <summary>
        /// Get the current elapsed time for this orbital body.
        /// Useful for external callers that need synchronized position/velocity calculations.
        /// </summary>
        public double GetElapsedTime()
        {
            return _elapsedTime;
        }
    }
}
