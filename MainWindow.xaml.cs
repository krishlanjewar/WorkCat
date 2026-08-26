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
    public enum CatState
    {
        Wandering,
        Hunting,
        Striking,
        Cooldown,
        Dragging
    }

    public enum RenderingMode
    {
        SpriteSheet,
        ProceduralVector
    }

    /// <summary>
    /// Implements the 60 FPS physics loop, finite state machine, sprite sheet frame playback, and navigation.
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Fields & Constants

        private readonly DispatcherTimer _gameTimer = new();
        private readonly DispatcherTimer _detectorTimer = new();
        private readonly DriftDetector _driftDetector = new();
        private readonly SpriteSheetLoader _spriteLoader = new();
        private readonly Random _random = new();

        private CatState _state = CatState.Wandering;
        private RenderingMode _renderMode = RenderingMode.SpriteSheet;
        private Storyboard? _pawSwipeStoryboard;

        // Position & Physics
        private double _posX = 200;
        private double _posY = 600;
        private double _groundY = 600;
        private int _facingDirection = 1; // 1 = Right, -1 = Left

        // Movement Speeds (Pixels/sec)
        private const double WalkSpeed = 105;
        private const double SprintSpeed = 480;
        private const double StrikeReachDistance = 55;

        // Wandering / Idle Sub-State
        private bool _isIdling = false;
        private double _idleTimeRemaining = 0;
        private double _walkTimeRemaining = 4.0;

        // Hunting Target
        private DriftTarget? _currentTarget;

        // Timers & Time Tracking
        private double _totalElapsedSeconds = 0;
        private double _cooldownTimeRemaining = 0;
        private double _pauseDetectionTimeRemaining = 0;
        private readonly Stopwatch _frameStopwatch = new();

        // Drag & Drop
        private bool _isDragging = false;
        private Point _dragOffset;

        // Screen Boundaries
        private double _screenWidth = 1920;
        private double _screenHeight = 1080;

        #endregion

        public MainWindow()
        {
            InitializeComponent();
        }

        #region Lifecycle Events

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _screenWidth = SystemParameters.PrimaryScreenWidth;
            _screenHeight = SystemParameters.PrimaryScreenHeight;
            _groundY = _screenHeight - 130;
            _posY = _groundY;
            _posX = Math.Min(300, _screenWidth / 4);

            UpdateCatCanvasPosition();

            _pawSwipeStoryboard = (Storyboard)Resources["PawSwipeStoryboard"];

            // Load and slice Sprite Sheet
            bool loaded = _spriteLoader.LoadAndSlice();
            if (!loaded)
            {
                // Fallback to Procedural Vector if sprite asset is missing
                _renderMode = RenderingMode.ProceduralVector;
            }

            ApplyRenderingMode();

            // 1. 60 FPS Render & Physics Loop (16.6 ms)
            _frameStopwatch.Start();
            _gameTimer.Interval = TimeSpan.FromMilliseconds(16.6);
            _gameTimer.Tick += GameLoop_Tick;
            _gameTimer.Start();

            // 2. Drift Content Scanner Loop (~350 ms)
            _detectorTimer.Interval = TimeSpan.FromMilliseconds(350);
            _detectorTimer.Tick += DetectorLoop_Tick;
            _detectorTimer.Start();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _gameTimer.Stop();
            _detectorTimer.Stop();
        }

        #endregion

        #region Game & Physics Loop (60 FPS)

        private void GameLoop_Tick(object? sender, EventArgs e)
        {
            double deltaTime = _frameStopwatch.Elapsed.TotalSeconds;
            _frameStopwatch.Restart();

            if (deltaTime > 0.1) deltaTime = 0.016;
            _totalElapsedSeconds += deltaTime;

            if (_pauseDetectionTimeRemaining > 0)
            {
                _pauseDetectionTimeRemaining -= deltaTime;
            }

            switch (_state)
            {
                case CatState.Wandering:
                    UpdateWanderingState(deltaTime);
                    break;

                case CatState.Hunting:
                    UpdateHuntingState(deltaTime);
                    break;

                case CatState.Striking:
                    // Handled asynchronously in ExecuteStrike
                    break;

                case CatState.Cooldown:
                    UpdateCooldownState(deltaTime);
                    break;

                case CatState.Dragging:
                    break;
            }

            UpdateCatCanvasPosition();
        }

        #endregion

        #region State Machine Implementations

        private void UpdateWanderingState(double dt)
        {
            if (_isIdling)
            {
                _idleTimeRemaining -= dt;

                if (_renderMode == RenderingMode.SpriteSheet && _spriteLoader.WalkFrames.Count > 0)
                {
                    CatSpriteFrameImage.Source = _spriteLoader.WalkFrames[0];
                }
                else
                {
                    ApplyIdleProceduralAnimation(_totalElapsedSeconds);
                }

                if (_idleTimeRemaining <= 0)
                {
                    _isIdling = false;
                    _walkTimeRemaining = 3.0 + (_random.NextDouble() * 5.0);
                    _facingDirection = _random.Next(0, 2) == 0 ? -1 : 1;
                }
            }
            else
            {
                _walkTimeRemaining -= dt;
                _posX += _facingDirection * WalkSpeed * dt;

                // Screen edge bouncing
                if (_posX < 40)
                {
                    _posX = 40;
                    _facingDirection = 1;
                }
                else if (_posX > _screenWidth - 160)
                {
                    _posX = _screenWidth - 160;
                    _facingDirection = -1;
                }

                // Smoothly return to ground baseline
                if (Math.Abs(_posY - _groundY) > 2)
                {
                    _posY += (_groundY - _posY) * 8 * dt;
                }

                if (_renderMode == RenderingMode.SpriteSheet && _spriteLoader.WalkFrames.Count > 0)
                {
                    int frameIndex = (int)(_totalElapsedSeconds * 6.0) % _spriteLoader.WalkFrames.Count;
                    CatSpriteFrameImage.Source = _spriteLoader.WalkFrames[frameIndex];
                }
                else
                {
                    ApplyWalkCycleProceduralAnimation(_totalElapsedSeconds, walkSpeedFactor: 1.0);
                }

                if (_walkTimeRemaining <= 0)
                {
                    _isIdling = true;
                    _idleTimeRemaining = 1.5 + (_random.NextDouble() * 3.0);
                }
            }

            CatScaleTransform.ScaleX = _facingDirection;
        }

        private void UpdateHuntingState(double dt)
        {
            if (_currentTarget == null)
            {
                _state = CatState.Wandering;
                HideStatusBubble();
                return;
            }

            double targetX = _currentTarget.StrikePoint.X - 65;
            double targetY = _currentTarget.StrikePoint.Y - 50;

            double dx = targetX - _posX;
            double dy = targetY - _posY;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));

            _facingDirection = dx >= 0 ? 1 : -1;
            CatScaleTransform.ScaleX = _facingDirection;

            if (distance <= StrikeReachDistance)
            {
                ExecuteStrike();
            }
            else
            {
                double dirX = dx / distance;
                double dirY = dy / distance;

                _posX += dirX * SprintSpeed * dt;
                _posY += dirY * SprintSpeed * dt;

                if (_renderMode == RenderingMode.SpriteSheet && _spriteLoader.SprintFrames.Count > 0)
                {
                    int frameIndex = (int)(_totalElapsedSeconds * 12.0) % _spriteLoader.SprintFrames.Count;
                    CatSpriteFrameImage.Source = _spriteLoader.SprintFrames[frameIndex];
                }
                else
                {
                    ApplyWalkCycleProceduralAnimation(_totalElapsedSeconds, walkSpeedFactor: 2.4);
                }

                Canvas.SetLeft(StatusBubble, Math.Max(20, Math.Min(_screenWidth - 180, _posX - 20)));
                Canvas.SetTop(StatusBubble, Math.Max(20, _posY - 35));
            }
        }

        private async void ExecuteStrike()
        {
            _state = CatState.Striking;
            StatusText.Text = "SWIPING ACTIVE TAB! 🐾";

            // 1. Play Strike Visuals & Paw Swipe
            _pawSwipeStoryboard?.Begin();
            CatTranslateTransform.Y = -14;

            // Frame sequence for sprite sheet
            if (_renderMode == RenderingMode.SpriteSheet && _spriteLoader.StrikeFrames.Count >= 3)
            {
                CatSpriteFrameImage.Source = _spriteLoader.StrikeFrames[0];
                await Task.Delay(70);
                CatSpriteFrameImage.Source = _spriteLoader.StrikeFrames[1];
                await Task.Delay(80);
                CatSpriteFrameImage.Source = _spriteLoader.StrikeFrames[2];
            }
            else
            {
                await Task.Delay(150);
            }

            // 2. Dispatch Synthetic Ctrl+W via Win32
            if (_currentTarget != null)
            {
                Win32Helper.SendCtrlW(_currentTarget.Hwnd);
            }

            await Task.Delay(250);

            CatTranslateTransform.Y = 0;

            // 3. Cooldown grace period (4 seconds)
            _state = CatState.Cooldown;
            _cooldownTimeRemaining = 4.0;
            _currentTarget = null;

            StatusText.Text = "TAB CLOSED! RESTING 💤";
            await Task.Delay(1200);
            HideStatusBubble();
        }

        private void UpdateCooldownState(double dt)
        {
            _cooldownTimeRemaining -= dt;

            if (Math.Abs(_posY - _groundY) > 2)
            {
                _posY += (_groundY - _posY) * 5 * dt;
            }

            if (_renderMode == RenderingMode.SpriteSheet && _spriteLoader.WalkFrames.Count > 0)
            {
                CatSpriteFrameImage.Source = _spriteLoader.WalkFrames[0];
            }
            else
            {
                ApplyIdleProceduralAnimation(_totalElapsedSeconds * 0.7);
            }

            if (_cooldownTimeRemaining <= 0)
            {
                _state = CatState.Wandering;
                _isIdling = true;
                _idleTimeRemaining = 2.0;
            }
        }

        #endregion

        #region Drift Detector Routine

        private async void DetectorLoop_Tick(object? sender, EventArgs e)
        {
            if (_state != CatState.Wandering || _pauseDetectionTimeRemaining > 0) return;

            try
            {
                var target = await _driftDetector.CheckForegroundWindowAsync();
                if (target != null)
                {
                    _currentTarget = target;
                    _state = CatState.Hunting;

                    StatusText.Text = $"DRIFT DETECTED: {target.MatchedPattern}";
                    ShowStatusBubble();
                }
            }
            catch { }
        }

        #endregion

        #region Procedural Animations & Render Modes

        private void ApplyRenderingMode()
        {
            if (_renderMode == RenderingMode.SpriteSheet && _spriteLoader.IsLoaded)
            {
                CatSpriteFrameImage.Visibility = Visibility.Visible;
                CatVectorContainer.Visibility = Visibility.Collapsed;
                if (_spriteLoader.WalkFrames.Count > 0)
                {
                    CatSpriteFrameImage.Source = _spriteLoader.WalkFrames[0];
                }
            }
            else
            {
                CatSpriteFrameImage.Visibility = Visibility.Collapsed;
                CatVectorContainer.Visibility = Visibility.Visible;
            }
        }

        private void ApplyWalkCycleProceduralAnimation(double time, double walkSpeedFactor)
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
            ShadowScaleTransform.ScaleX = 1.0 - (Math.Abs(phase1) * 0.15);
        }

        private void ApplyIdleProceduralAnimation(double time)
        {
            LegBackLeftRotate.Angle = 0;
            LegBackRightRotate.Angle = 0;
            LegFrontLeftRotate.Angle = 0;
            StrikingPawTransform.Angle = 0;
            CatTranslateTransform.Y = 0;
            ShadowScaleTransform.ScaleX = 1.0;

            TailRotateTransform.Angle = Math.Sin(time * 2.5) * 14.0;
        }

        private void UpdateCatCanvasPosition()
        {
            Canvas.SetLeft(CatRoot, _posX);
            Canvas.SetTop(CatRoot, _posY);
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

        #region Interactive Drag and Drop

        private void CatRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _state = CatState.Dragging;
            CatRoot.CaptureMouse();
            _dragOffset = e.GetPosition(CatRoot);
            CatScaleTransform.ScaleY = 1.1;
        }

        private void CatRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point screenPos = e.GetPosition(OverlayCanvas);
                _posX = screenPos.X - _dragOffset.X;
                _posY = screenPos.Y - _dragOffset.Y;

                _posX = Math.Max(10, Math.Min(_screenWidth - 140, _posX));
                _posY = Math.Max(10, Math.Min(_screenHeight - 110, _posY));

                UpdateCatCanvasPosition();
            }
        }

        private void CatRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                CatRoot.ReleaseMouseCapture();
                CatScaleTransform.ScaleY = 1.0;

                _groundY = Math.Max(_screenHeight - 180, _posY);
                _state = CatState.Wandering;
                _isIdling = true;
                _idleTimeRemaining = 1.5;
            }
        }

        #endregion

        #region Context Menu Handlers

        private void MenuToggleStyle_Click(object sender, RoutedEventArgs e)
        {
            _renderMode = _renderMode == RenderingMode.SpriteSheet ? RenderingMode.ProceduralVector : RenderingMode.SpriteSheet;
            ApplyRenderingMode();
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
                    MatchedPattern = "Manual Strike Test",
                    WindowRect = rect,
                    StrikePoint = new Point(rect.CenterX, rect.Top + 50)
                };
                _state = CatState.Hunting;
                StatusText.Text = "MANUAL STRIKE TRIGGERED! 🎯";
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

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        #endregion
    }
}
