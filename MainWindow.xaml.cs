using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WorkCat
{
    public enum ActivePet
    {
        Cat,
        Eagle
    }

    public enum CatState
    {
        Wandering,
        Hunting,
        Striking,
        Cooldown,
        Dragging
    }

    public enum EagleState
    {
        Idle,
        Walking,
        PreparingToFly,
        Takeoff,
        Flying,
        Landing,
        Perched,
        Curious,
        LookingAtUser,
        Angry,
        Grabbed,
        Hunting,
        Striking,
        Cooldown
    }

    public enum CatRenderingMode
    {
        SpriteSheet,
        ProceduralVector
    }

    /// <summary>
    /// Dual Desktop Pet Controller (Chibi Cat & Autonomous Eagle) with 60 FPS physics,
    /// behavioral state machine, flight physics, and UIAutomation anti-drift system.
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Core Fields & Timers

        private readonly DispatcherTimer _gameTimer = new();
        private readonly DispatcherTimer _detectorTimer = new();
        private readonly DriftDetector _driftDetector = new();
        private readonly SpriteSheetLoader _catSpriteLoader = new();
        private readonly EagleSpriteLoader _eagleSpriteLoader = new();
        private readonly Random _random = new();

        private ActivePet _activePet = ActivePet.Cat; // Default to the WorkCat chibi cat

        // Anti-Drift Target & Detection
        private DriftTarget? _currentTarget;
        private double _pauseDetectionTimeRemaining = 0;

        // Screen Boundaries
        private double _screenWidth = 1920;
        private double _screenHeight = 1080;
        private double _groundY = 600;

        private double _totalElapsedSeconds = 0;
        private readonly Stopwatch _frameStopwatch = new();

        #endregion

        #region Cat Fields

        private CatState _catState = CatState.Wandering;
        private CatRenderingMode _catRenderMode = CatRenderingMode.ProceduralVector;
        private Storyboard? _pawSwipeStoryboard;

        private double _catPosX = 200;
        private double _catPosY = 600;
        private int _catFacingDirection = 1;

        private bool _catIsIdling = false;
        private double _catIdleTimeRemaining = 0;
        private double _catWalkTimeRemaining = 4.0;
        private double _catCooldownTimeRemaining = 0;

        private bool _catIsDragging = false;
        private Point _catDragOffset;

        private const double CatWalkSpeed = 105;
        private const double CatSprintSpeed = 480;
        private const double CatStrikeReachDistance = 55;

        #endregion

        #region Eagle Fields

        private EagleState _eagleState = EagleState.Idle;

        private double _eaglePosX = 400;
        private double _eaglePosY = 600;
        private double _eagleBaseAltitudeY = 600;
        private double _eagleTargetX = 400;
        private double _eagleTargetY = 600;
        private int _eagleFacingDirection = 1;

        // Eagle Timing & Personality
        private double _eagleStateTimer = 0;
        private double _eagleIdleSubFrameTimer = 0;
        private int _eagleIdleSubFrame = 0;
        private int _eagleRapidClickCount = 0;
        private double _eagleClickCooldownTimer = 0;
        private double _eagleCooldownRemaining = 0;

        private bool _eagleIsDragging = false;
        private Point _eagleDragOffset;

        private const double EagleWalkSpeed = 55;
        private const double EagleFlightSpeed = 340;
        private const double EagleRaptorDiveSpeed = 620;

        // Flying animation sequence: 4 -> 5 -> 6 -> 7 -> 8 -> 9 -> 7 -> 6 -> 5
        private readonly int[] _eagleFlightCycle = { 3, 4, 5, 6, 7, 8, 6, 5, 4 };

        #endregion

        public MainWindow()
        {
            InitializeComponent();
        }

        #region Lifecycle

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _screenWidth = SystemParameters.PrimaryScreenWidth;
            _screenHeight = SystemParameters.PrimaryScreenHeight;
            _groundY = _screenHeight - 145;

            _catPosY = _groundY;
            _catPosX = Math.Min(250, _screenWidth / 4);

            _eaglePosY = _groundY;
            _eagleBaseAltitudeY = _groundY;
            _eaglePosX = Math.Min(500, _screenWidth / 2);

            _pawSwipeStoryboard = (Storyboard)Resources["PawSwipeStoryboard"];

            // Load sprite sheets
            _catSpriteLoader.LoadAndSlice();
            _eagleSpriteLoader.LoadAndSlice();

            UpdatePetVisibility();
            ApplyCatRenderingMode();
            UpdateStartupMenuCheckmarks();

            // Start 60 FPS Game Loop
            _frameStopwatch.Start();
            _gameTimer.Interval = TimeSpan.FromMilliseconds(16.6);
            _gameTimer.Tick += GameLoop_Tick;
            _gameTimer.Start();

            // Start Anti-Drift Scanner
            _detectorTimer.Interval = TimeSpan.FromMilliseconds(350);
            _detectorTimer.Tick += DetectorLoop_Tick;
            _detectorTimer.Start();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _gameTimer.Stop();
            _detectorTimer.Stop();
        }

        private void UpdatePetVisibility()
        {
            if (_activePet == ActivePet.Cat)
            {
                CatRoot.Visibility = Visibility.Visible;
                EagleRoot.Visibility = Visibility.Collapsed;
            }
            else
            {
                CatRoot.Visibility = Visibility.Collapsed;
                EagleRoot.Visibility = Visibility.Visible;
            }
        }

        #endregion

        #region Main Loop (60 FPS)

        private void GameLoop_Tick(object? sender, EventArgs e)
        {
            double dt = _frameStopwatch.Elapsed.TotalSeconds;
            _frameStopwatch.Restart();
            if (dt > 0.1) dt = 0.016;
            _totalElapsedSeconds += dt;

            if (_pauseDetectionTimeRemaining > 0)
                _pauseDetectionTimeRemaining -= dt;

            if (_activePet == ActivePet.Cat)
            {
                UpdateCat(dt);
            }
            else
            {
                UpdateEagle(dt);
            }
        }

        #endregion

        #region Eagle Autonomous AI & Physics Loop

        private void UpdateEagle(double dt)
        {
            if (_eagleClickCooldownTimer > 0)
            {
                _eagleClickCooldownTimer -= dt;
                if (_eagleClickCooldownTimer <= 0) _eagleRapidClickCount = 0;
            }

            switch (_eagleState)
            {
                case EagleState.Idle:
                    UpdateEagleIdle(dt);
                    break;

                case EagleState.Walking:
                    UpdateEagleWalking(dt);
                    break;

                case EagleState.PreparingToFly:
                    UpdateEaglePreparingToFly(dt);
                    break;

                case EagleState.Takeoff:
                    UpdateEagleTakeoff(dt);
                    break;

                case EagleState.Flying:
                    UpdateEagleFlying(dt);
                    break;

                case EagleState.Landing:
                    UpdateEagleLanding(dt);
                    break;

                case EagleState.Perched:
                    UpdateEaglePerched(dt);
                    break;

                case EagleState.Curious:
                    UpdateEagleCurious(dt);
                    break;

                case EagleState.LookingAtUser:
                    UpdateEagleLookingAtUser(dt);
                    break;

                case EagleState.Angry:
                    UpdateEagleAngry(dt);
                    break;

                case EagleState.Hunting:
                    UpdateEagleHunting(dt);
                    break;

                case EagleState.Striking:
                    // Async strike coordination
                    break;

                case EagleState.Cooldown:
                    UpdateEagleCooldown(dt);
                    break;

                case EagleState.Grabbed:
                    break;
            }

            UpdateEagleTransformAndPosition();
        }

        private void UpdateEagleIdle(double dt)
        {
            _eagleStateTimer -= dt;
            _eagleIdleSubFrameTimer -= dt;

            // Subtle breathing and head-turn sub-frames
            if (_eagleIdleSubFrameTimer <= 0 && _eagleSpriteLoader.IsLoaded)
            {
                _eagleIdleSubFrameTimer = 0.6 + (_random.NextDouble() * 0.8);
                _eagleIdleSubFrame = _random.Next(0, 3);
                EagleSpriteImage.Source = _eagleIdleSubFrame switch
                {
                    0 => _eagleSpriteLoader.StandNormal,
                    1 => _eagleSpriteLoader.StandAlert,
                    _ => _eagleSpriteLoader.StandNormal
                };
            }

            // Ground settling
            if (Math.Abs(_eaglePosY - _groundY) > 2)
            {
                _eaglePosY += (_groundY - _eaglePosY) * 6 * dt;
            }

            // Autonomous Behavior Decisions when idle duration expires
            if (_eagleStateTimer <= 0)
            {
                DecideEagleNextAutonomousAction();
            }
        }

        private void DecideEagleNextAutonomousAction()
        {
            int roll = _random.Next(0, 100);

            if (roll < 45)
            {
                // Remain Idle
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 2.0 + (_random.NextDouble() * 3.5);
                if (_random.NextDouble() < 0.3) _eagleFacingDirection *= -1;
            }
            else if (roll < 65)
            {
                // Walk along ground
                _eagleState = EagleState.Walking;
                _eagleStateTimer = 2.5 + (_random.NextDouble() * 4.0);
                _eagleFacingDirection = _random.Next(0, 2) == 0 ? -1 : 1;
            }
            else if (roll < 80)
            {
                // Take Flight to a new random location
                PrepareEagleFlightTo(
                    targetX: _random.Next(80, (int)_screenWidth - 200),
                    targetY: _random.Next(100, (int)_groundY - 120)
                );
            }
            else if (roll < 90)
            {
                // Curious look down
                _eagleState = EagleState.Curious;
                _eagleStateTimer = 2.0 + (_random.NextDouble() * 2.0);
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.CuriousLookDown;
            }
            else if (roll < 96)
            {
                // Perched resting state
                _eagleState = EagleState.Perched;
                _eagleStateTimer = 8.0 + (_random.NextDouble() * 12.0);
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.Perched;
            }
            else
            {
                // Face directly at user
                _eagleState = EagleState.LookingAtUser;
                _eagleStateTimer = 2.0 + (_random.NextDouble() * 2.0);
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.FaceUser;
            }
        }

        private void UpdateEagleWalking(double dt)
        {
            _eagleStateTimer -= dt;
            _eaglePosX += _eagleFacingDirection * EagleWalkSpeed * dt;

            // Screen edge check
            if (_eaglePosX < 40)
            {
                _eaglePosX = 40;
                _eagleFacingDirection = 1;
            }
            else if (_eaglePosX > _screenWidth - 160)
            {
                _eaglePosX = _screenWidth - 160;
                _eagleFacingDirection = -1;
            }

            // 3-Frame Walk Cycle
            if (_eagleSpriteLoader.IsLoaded)
            {
                int walkStep = (int)(_totalElapsedSeconds * 5.0) % 3;
                EagleSpriteImage.Source = walkStep switch
                {
                    0 => _eagleSpriteLoader.StandNormal,
                    1 => _eagleSpriteLoader.WalkStep1,
                    _ => _eagleSpriteLoader.WalkStep2
                };
            }

            if (_eagleStateTimer <= 0)
            {
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 1.5 + (_random.NextDouble() * 2.0);
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void PrepareEagleFlightTo(double targetX, double targetY)
        {
            _eagleTargetX = targetX;
            _eagleTargetY = targetY;
            _eagleBaseAltitudeY = _eaglePosY;

            _eagleState = EagleState.PreparingToFly;
            _eagleStateTimer = 0.55; // Windup duration

            if (_eagleSpriteLoader.IsLoaded)
                EagleSpriteImage.Source = _eagleSpriteLoader.TakeoffPrep;
        }

        private void UpdateEaglePreparingToFly(double dt)
        {
            _eagleStateTimer -= dt;
            if (_eagleStateTimer <= 0)
            {
                _eagleState = EagleState.Takeoff;
                _eagleStateTimer = 0.35;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.WingUp;
            }
        }

        private void UpdateEagleTakeoff(double dt)
        {
            _eagleStateTimer -= dt;
            _eaglePosY -= 220 * dt; // Rapid lift
            _eagleBaseAltitudeY = _eaglePosY;

            if (_eagleStateTimer <= 0)
            {
                _eagleState = EagleState.Flying;
            }
        }

        private void UpdateEagleFlying(double dt)
        {
            double dx = _eagleTargetX - _eaglePosX;
            double dy = _eagleTargetY - _eaglePosY;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));

            _eagleFacingDirection = dx >= 0 ? 1 : -1;

            if (distance < 50)
            {
                // Arrived: Begin Landing
                _eagleState = EagleState.Landing;
                _eagleStateTimer = 0.6;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.LandingTouch;
            }
            else
            {
                double dirX = dx / distance;
                double dirY = dy / distance;

                _eaglePosX += dirX * EagleFlightSpeed * dt;
                _eagleBaseAltitudeY += dirY * EagleFlightSpeed * dt;

                // Organic vertical sine-wave flight oscillation
                _eaglePosY = _eagleBaseAltitudeY + (Math.Sin(_totalElapsedSeconds * 7.5) * 14.0);

                // Flight Wing-Flap Animation Sequence
                if (_eagleSpriteLoader.IsLoaded)
                {
                    int index = (int)(_totalElapsedSeconds * 10.0) % _eagleFlightCycle.Length;
                    int frameIndex = _eagleFlightCycle[index];
                    EagleSpriteImage.Source = _eagleSpriteLoader.AllFrames[frameIndex];
                }
            }
        }

        private void UpdateEagleLanding(double dt)
        {
            _eagleStateTimer -= dt;
            _eaglePosY += 150 * dt;

            if (_eagleStateTimer <= 0 || _eaglePosY >= _groundY)
            {
                _eaglePosY = _groundY;
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 2.0 + (_random.NextDouble() * 3.0);
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void UpdateEaglePerched(double dt)
        {
            _eagleStateTimer -= dt;

            // Occasional look around while perched
            if (_random.Next(0, 150) == 1 && _eagleSpriteLoader.IsLoaded)
            {
                _eagleFacingDirection *= -1;
            }

            if (_eagleStateTimer <= 0)
            {
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 2.0;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void UpdateEagleCurious(double dt)
        {
            _eagleStateTimer -= dt;
            if (_eagleStateTimer <= 0)
            {
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 1.5;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void UpdateEagleLookingAtUser(double dt)
        {
            _eagleStateTimer -= dt;
            if (_eagleStateTimer <= 0)
            {
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 2.0;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void UpdateEagleAngry(double dt)
        {
            _eagleStateTimer -= dt;

            // Ruffled agitation vibration
            EagleTranslateTransform.X = (Math.Sin(_totalElapsedSeconds * 30.0) * 3.0);

            if (_eagleStateTimer <= 0)
            {
                EagleTranslateTransform.X = 0;
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 2.0;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void UpdateEagleHunting(double dt)
        {
            if (_currentTarget == null)
            {
                _eagleState = EagleState.Idle;
                HideStatusBubble();
                return;
            }

            double targetX = _currentTarget.StrikePoint.X - 70;
            double targetY = _currentTarget.StrikePoint.Y - 50;

            double dx = targetX - _eaglePosX;
            double dy = targetY - _eaglePosY;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));

            _eagleFacingDirection = dx >= 0 ? 1 : -1;

            if (distance <= 60)
            {
                ExecuteEagleStrike();
            }
            else
            {
                double dirX = dx / distance;
                double dirY = dy / distance;

                // High speed raptor swoop
                _eaglePosX += dirX * EagleRaptorDiveSpeed * dt;
                _eaglePosY += dirY * EagleRaptorDiveSpeed * dt;

                if (_eagleSpriteLoader.IsLoaded)
                {
                    EagleSpriteImage.Source = _eagleSpriteLoader.Swoop;
                }

                Canvas.SetLeft(StatusBubble, Math.Max(20, Math.Min(_screenWidth - 200, _eaglePosX - 10)));
                Canvas.SetTop(StatusBubble, Math.Max(20, _eaglePosY - 35));
            }
        }

        private async void ExecuteEagleStrike()
        {
            _eagleState = EagleState.Striking;
            StatusEmoji.Text = "🦅 ";
            StatusText.Text = "RAPTOR DIVE! SNATCHING TAB!";

            if (_eagleSpriteLoader.IsLoaded)
            {
                EagleSpriteImage.Source = _eagleSpriteLoader.Angry; // Ruffled screech attack
            }

            await Task.Delay(180);

            // Synthetic Keystroke Dispatch via Win32
            if (_currentTarget != null)
            {
                Win32Helper.SendCtrlW(_currentTarget.Hwnd);
            }

            await Task.Delay(300);

            // Transition to glide recovery & cooldown
            _eagleState = EagleState.Cooldown;
            _eagleCooldownRemaining = 4.0;
            _currentTarget = null;

            StatusText.Text = "TARGET ELIMINATED! 🦅✨";
            await Task.Delay(1200);
            HideStatusBubble();

            // Fly back down to perch/ground
            _eagleTargetX = _random.Next(150, (int)_screenWidth - 250);
            _eagleTargetY = _groundY;
        }

        private void UpdateEagleCooldown(double dt)
        {
            _eagleCooldownRemaining -= dt;

            // Descend smoothly to ground
            if (Math.Abs(_eaglePosY - _groundY) > 2)
            {
                _eaglePosY += (_groundY - _eaglePosY) * 4 * dt;
            }

            if (_eagleSpriteLoader.IsLoaded)
            {
                EagleSpriteImage.Source = _eagleSpriteLoader.GlideOpen;
            }

            if (_eagleCooldownRemaining <= 0)
            {
                _eagleState = EagleState.Idle;
                _eagleStateTimer = 2.0;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.StandNormal;
            }
        }

        private void UpdateEagleTransformAndPosition()
        {
            EagleScaleTransform.ScaleX = _eagleFacingDirection;
            Canvas.SetLeft(EagleRoot, _eaglePosX);
            Canvas.SetTop(EagleRoot, _eaglePosY);

            // Shadow scales dynamically with altitude
            double altitude = Math.Max(0, _groundY - _eaglePosY);
            double shadowScale = Math.Max(0.2, 1.0 - (altitude / 600.0));
            EagleShadowScaleTransform.ScaleX = shadowScale;
            EagleShadowScaleTransform.ScaleY = shadowScale;
            EagleShadow.Opacity = Math.Max(0.05, 0.4 - (altitude / 1000.0));
        }

        #endregion

        #region Cat AI & Physics Loop

        private void UpdateCat(double dt)
        {
            switch (_catState)
            {
                case CatState.Wandering:
                    UpdateCatWandering(dt);
                    break;

                case CatState.Hunting:
                    UpdateCatHunting(dt);
                    break;

                case CatState.Striking:
                    break;

                case CatState.Cooldown:
                    UpdateCatCooldown(dt);
                    break;

                case CatState.Dragging:
                    break;
            }

            CatScaleTransform.ScaleX = _catFacingDirection;
            Canvas.SetLeft(CatRoot, _catPosX);
            Canvas.SetTop(CatRoot, _catPosY);
        }

        private void UpdateCatWandering(double dt)
        {
            if (_catIsIdling)
            {
                _catIdleTimeRemaining -= dt;

                if (_catRenderMode == CatRenderingMode.SpriteSheet && _catSpriteLoader.WalkFrames.Count > 0)
                {
                    CatSpriteFrameImage.Source = _catSpriteLoader.WalkFrames[0];
                }
                else
                {
                    ApplyCatIdleProceduralAnimation(_totalElapsedSeconds);
                }

                if (_catIdleTimeRemaining <= 0)
                {
                    _catIsIdling = false;
                    _catWalkTimeRemaining = 3.0 + (_random.NextDouble() * 5.0);
                    _catFacingDirection = _random.Next(0, 2) == 0 ? -1 : 1;
                }
            }
            else
            {
                _catWalkTimeRemaining -= dt;
                _catPosX += _catFacingDirection * CatWalkSpeed * dt;

                if (_catPosX < 40)
                {
                    _catPosX = 40;
                    _catFacingDirection = 1;
                }
                else if (_catPosX > _screenWidth - 160)
                {
                    _catPosX = _screenWidth - 160;
                    _catFacingDirection = -1;
                }

                if (Math.Abs(_catPosY - _groundY) > 2)
                {
                    _catPosY += (_groundY - _catPosY) * 8 * dt;
                }

                if (_catRenderMode == CatRenderingMode.SpriteSheet && _catSpriteLoader.WalkFrames.Count > 0)
                {
                    int frameIndex = (int)(_totalElapsedSeconds * 6.0) % _catSpriteLoader.WalkFrames.Count;
                    CatSpriteFrameImage.Source = _catSpriteLoader.WalkFrames[frameIndex];
                }
                else
                {
                    ApplyCatWalkProceduralAnimation(_totalElapsedSeconds, walkSpeedFactor: 1.0);
                }

                if (_catWalkTimeRemaining <= 0)
                {
                    _catIsIdling = true;
                    _catIdleTimeRemaining = 1.5 + (_random.NextDouble() * 3.0);
                }
            }
        }

        private void UpdateCatHunting(double dt)
        {
            if (_currentTarget == null)
            {
                _catState = CatState.Wandering;
                HideStatusBubble();
                return;
            }

            double targetX = _currentTarget.StrikePoint.X - 65;
            double targetY = _currentTarget.StrikePoint.Y - 50;

            double dx = targetX - _catPosX;
            double dy = targetY - _catPosY;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));

            _catFacingDirection = dx >= 0 ? 1 : -1;

            if (distance <= CatStrikeReachDistance)
            {
                ExecuteCatStrike();
            }
            else
            {
                double dirX = dx / distance;
                double dirY = dy / distance;

                _catPosX += dirX * CatSprintSpeed * dt;
                _catPosY += dirY * CatSprintSpeed * dt;

                if (_catRenderMode == CatRenderingMode.SpriteSheet && _catSpriteLoader.SprintFrames.Count > 0)
                {
                    int frameIndex = (int)(_totalElapsedSeconds * 12.0) % _catSpriteLoader.SprintFrames.Count;
                    CatSpriteFrameImage.Source = _catSpriteLoader.SprintFrames[frameIndex];
                }
                else
                {
                    ApplyCatWalkProceduralAnimation(_totalElapsedSeconds, walkSpeedFactor: 2.4);
                }

                Canvas.SetLeft(StatusBubble, Math.Max(20, Math.Min(_screenWidth - 180, _catPosX - 20)));
                Canvas.SetTop(StatusBubble, Math.Max(20, _catPosY - 35));
            }
        }

        private async void ExecuteCatStrike()
        {
            _catState = CatState.Striking;
            StatusEmoji.Text = "⚡ ";
            StatusText.Text = "SWIPING ACTIVE TAB! 🐾";

            _pawSwipeStoryboard?.Begin();
            CatTranslateTransform.Y = -14;

            if (_catRenderMode == CatRenderingMode.SpriteSheet && _catSpriteLoader.StrikeFrames.Count >= 3)
            {
                CatSpriteFrameImage.Source = _catSpriteLoader.StrikeFrames[0];
                await Task.Delay(70);
                CatSpriteFrameImage.Source = _catSpriteLoader.StrikeFrames[1];
                await Task.Delay(80);
                CatSpriteFrameImage.Source = _catSpriteLoader.StrikeFrames[2];
            }
            else
            {
                await Task.Delay(150);
            }

            if (_currentTarget != null)
            {
                Win32Helper.SendCtrlW(_currentTarget.Hwnd);
            }

            await Task.Delay(250);
            CatTranslateTransform.Y = 0;

            _catState = CatState.Cooldown;
            _catCooldownTimeRemaining = 4.0;
            _currentTarget = null;

            StatusText.Text = "TAB CLOSED! RESTING 💤";
            await Task.Delay(1200);
            HideStatusBubble();
        }

        private void UpdateCatCooldown(double dt)
        {
            _catCooldownTimeRemaining -= dt;

            if (Math.Abs(_catPosY - _groundY) > 2)
            {
                _catPosY += (_groundY - _catPosY) * 5 * dt;
            }

            if (_catRenderMode == CatRenderingMode.SpriteSheet && _catSpriteLoader.WalkFrames.Count > 0)
            {
                CatSpriteFrameImage.Source = _catSpriteLoader.WalkFrames[0];
            }
            else
            {
                ApplyCatIdleProceduralAnimation(_totalElapsedSeconds * 0.7);
            }

            if (_catCooldownTimeRemaining <= 0)
            {
                _catState = CatState.Wandering;
                _catIsIdling = true;
                _catIdleTimeRemaining = 2.0;
            }
        }

        private void ApplyCatRenderingMode()
        {
            if (_catRenderMode == CatRenderingMode.SpriteSheet && _catSpriteLoader.IsLoaded)
            {
                CatSpriteFrameImage.Visibility = Visibility.Visible;
                CatVectorContainer.Visibility = Visibility.Collapsed;
                if (_catSpriteLoader.WalkFrames.Count > 0)
                    CatSpriteFrameImage.Source = _catSpriteLoader.WalkFrames[0];
            }
            else
            {
                CatSpriteFrameImage.Visibility = Visibility.Collapsed;
                CatVectorContainer.Visibility = Visibility.Visible;
            }
        }

        private void ApplyCatWalkProceduralAnimation(double time, double walkSpeedFactor)
        {
            double frequency = 12.0 * walkSpeedFactor;
            double legSwingAngle = 24.0;

            double phase1 = Math.Sin(time * frequency);
            double phase2 = Math.Sin((time * frequency) + Math.PI);

            LegBackLeftRotate.Angle = phase1 * legSwingAngle;
            LegBackRightRotate.Angle = phase2 * legSwingAngle;
            LegFrontLeftRotate.Angle = phase2 * legSwingAngle;
            StrikingPawTransform.Angle = phase1 * (legSwingAngle * 0.8);

            CatTranslateTransform.Y = -Math.Abs(phase1) * (3.5 * walkSpeedFactor);
            TailRotateTransform.Angle = Math.Sin(time * 8.0) * 18.0;
            CatShadowScaleTransform.ScaleX = 1.0 - (Math.Abs(phase1) * 0.15);
        }

        private void ApplyCatIdleProceduralAnimation(double time)
        {
            LegBackLeftRotate.Angle = 0;
            LegBackRightRotate.Angle = 0;
            LegFrontLeftRotate.Angle = 0;
            StrikingPawTransform.Angle = 0;
            CatTranslateTransform.Y = 0;
            CatShadowScaleTransform.ScaleX = 1.0;
            TailRotateTransform.Angle = Math.Sin(time * 2.5) * 14.0;
        }

        #endregion

        #region Anti-Drift Detector Routine

        private async void DetectorLoop_Tick(object? sender, EventArgs e)
        {
            if (_pauseDetectionTimeRemaining > 0) return;

            bool isReadyForHunt = _activePet == ActivePet.Cat
                ? _catState == CatState.Wandering
                : (_eagleState == EagleState.Idle || _eagleState == EagleState.Walking || _eagleState == EagleState.Perched);

            if (!isReadyForHunt) return;

            try
            {
                var target = await _driftDetector.CheckForegroundWindowAsync();
                if (target != null)
                {
                    _currentTarget = target;

                    if (_activePet == ActivePet.Cat)
                    {
                        _catState = CatState.Hunting;
                        StatusEmoji.Text = "⚡ ";
                        StatusText.Text = $"DRIFT DETECTED: {target.MatchedPattern}";
                    }
                    else
                    {
                        _eagleState = EagleState.Hunting;
                        StatusEmoji.Text = "🦅 ";
                        StatusText.Text = $"RAPTOR SPOTTED DRIFT: {target.MatchedPattern}";
                    }

                    ShowStatusBubble();
                }
            }
            catch { }
        }

        private void ShowStatusBubble()
        {
            StatusBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        private void HideStatusBubble()
        {
            StatusBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)));
        }

        #endregion

        #region Mouse Interactions (Cat & Eagle)

        private void CatRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _catIsDragging = true;
            _catState = CatState.Dragging;
            CatRoot.CaptureMouse();
            _catDragOffset = e.GetPosition(CatRoot);
            CatScaleTransform.ScaleY = 1.1;
        }

        private void CatRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (_catIsDragging)
            {
                Point screenPos = e.GetPosition(OverlayCanvas);
                _catPosX = screenPos.X - _catDragOffset.X;
                _catPosY = screenPos.Y - _catDragOffset.Y;

                _catPosX = Math.Max(10, Math.Min(_screenWidth - 140, _catPosX));
                _catPosY = Math.Max(10, Math.Min(_screenHeight - 110, _catPosY));

                Canvas.SetLeft(CatRoot, _catPosX);
                Canvas.SetTop(CatRoot, _catPosY);
            }
        }

        private void CatRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_catIsDragging)
            {
                _catIsDragging = false;
                CatRoot.ReleaseMouseCapture();
                CatScaleTransform.ScaleY = 1.0;

                _groundY = Math.Max(_screenHeight - 180, _catPosY);
                _catState = CatState.Wandering;
                _catIsIdling = true;
                _catIdleTimeRemaining = 1.5;
            }
        }

        private void EagleRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _eagleRapidClickCount++;
            _eagleClickCooldownTimer = 1.2;

            if (_eagleRapidClickCount >= 3)
            {
                // Rapid clicks trigger Angry / Startled state
                _eagleState = EagleState.Angry;
                _eagleStateTimer = 2.0;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.Angry;
                return;
            }

            _eagleIsDragging = true;
            _eagleState = EagleState.Grabbed;
            EagleRoot.CaptureMouse();
            _eagleDragOffset = e.GetPosition(EagleRoot);

            if (_eagleSpriteLoader.IsLoaded)
                EagleSpriteImage.Source = _eagleSpriteLoader.WingUp; // Wings spread while held
        }

        private void EagleRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (_eagleIsDragging)
            {
                Point screenPos = e.GetPosition(OverlayCanvas);
                _eaglePosX = screenPos.X - _eagleDragOffset.X;
                _eaglePosY = screenPos.Y - _eagleDragOffset.Y;

                _eaglePosX = Math.Max(10, Math.Min(_screenWidth - 150, _eaglePosX));
                _eaglePosY = Math.Max(10, Math.Min(_screenHeight - 150, _eaglePosY));

                UpdateEagleTransformAndPosition();
            }
        }

        private void EagleRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_eagleIsDragging)
            {
                _eagleIsDragging = false;
                EagleRoot.ReleaseMouseCapture();

                // On release: Glide smoothly down to ground baseline
                _eagleBaseAltitudeY = _eaglePosY;
                _eagleState = EagleState.Landing;
                _eagleStateTimer = 0.8;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.LandingTouch;
            }
            else if (_eagleState == EagleState.Idle)
            {
                // Single click interaction: Faces user
                _eagleState = EagleState.LookingAtUser;
                _eagleStateTimer = 2.0;
                if (_eagleSpriteLoader.IsLoaded)
                    EagleSpriteImage.Source = _eagleSpriteLoader.FaceUser;
            }
        }

        #endregion

        #region Context Menu Handlers

        private void MenuSwitchPet_Click(object sender, RoutedEventArgs e)
        {
            _activePet = _activePet == ActivePet.Cat ? ActivePet.Eagle : ActivePet.Cat;
            UpdatePetVisibility();
        }

        private void MenuEagleFly_Click(object sender, RoutedEventArgs e)
        {
            PrepareEagleFlightTo(
                targetX: _random.Next(100, (int)_screenWidth - 200),
                targetY: _random.Next(80, (int)_groundY - 150)
            );
        }

        private void MenuEaglePerch_Click(object sender, RoutedEventArgs e)
        {
            _eagleState = EagleState.Perched;
            _eagleStateTimer = 15.0;
            if (_eagleSpriteLoader.IsLoaded)
                EagleSpriteImage.Source = _eagleSpriteLoader.Perched;
        }

        private void MenuToggleCatStyle_Click(object sender, RoutedEventArgs e)
        {
            _catRenderMode = _catRenderMode == CatRenderingMode.SpriteSheet
                ? CatRenderingMode.ProceduralVector
                : CatRenderingMode.SpriteSheet;
            ApplyCatRenderingMode();
        }

        private void MenuTestStrike_Click(object sender, RoutedEventArgs e)
        {
            IntPtr fg = Win32Helper.GetForegroundWindow();
            if (fg != IntPtr.Zero && Win32Helper.GetWindowRect(fg, out Win32Helper.RECT rect))
            {
                _currentTarget = new DriftTarget
                {
                    Hwnd = fg,
                    Title = Win32Helper.GetWindowTitle(fg),
                    MatchedPattern = "Manual Strike Command",
                    WindowRect = rect,
                    StrikePoint = new Point(rect.CenterX, rect.Top + 50)
                };

                if (_activePet == ActivePet.Cat)
                {
                    _catState = CatState.Hunting;
                }
                else
                {
                    _eagleState = EagleState.Hunting;
                }

                StatusEmoji.Text = _activePet == ActivePet.Cat ? "🐾 " : "🦅 ";
                StatusText.Text = "MANUAL STRIKE DISPATCHED!";
                ShowStatusBubble();
            }
        }

        private void MenuPauseDetection_Click(object sender, RoutedEventArgs e)
        {
            _pauseDetectionTimeRemaining = 300; // 5 minutes
            StatusText.Text = "DETECTION PAUSED (5 MIN)";
            ShowStatusBubble();
            Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(HideStatusBubble));
        }

        private void MenuStartup_Click(object sender, RoutedEventArgs e)
        {
            bool current = IsStartupEnabled();
            bool newState = !current;
            SetStartup(newState);
            UpdateStartupMenuCheckmarks();

            StatusEmoji.Text = "🚀 ";
            StatusText.Text = newState ? "AUTO-START ENABLED!" : "AUTO-START DISABLED!";
            ShowStatusBubble();
            Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(HideStatusBubble));
        }

        private void UpdateStartupMenuCheckmarks()
        {
            bool isEnabled = IsStartupEnabled();
            MenuCatStartup.IsChecked = isEnabled;
            MenuEagleStartup.IsChecked = isEnabled;
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("WorkCat") != null;
            }
            catch
            {
                return false;
            }
        }

        private static void SetStartup(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName 
                        ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkCat.exe");
                    key.SetValue("WorkCat", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("WorkCat", false);
                }
            }
            catch
            {
                // Ignore permissions
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        #endregion
    }
}
