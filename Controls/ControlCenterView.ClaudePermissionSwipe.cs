using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ClaudeCode;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private PermissionSwipeCard? _swipingClaudePermission;

    internal sealed class PermissionSwipeCard : Border
    {
        private const double Slop = 6;
        private const double Threshold = 64;
        private static readonly Brush AllowInk = Frozen("#A6C9A0");
        private static readonly Brush DenyInk = Frozen("#D98A84");

        private readonly ControlCenterView _view;
        private readonly string _id;
        private readonly TranslateTransform _shift = new();
        private readonly TextBlock _outcome;
        private Grid _content = null!;
        private Point? _origin;
        private int _sampleTime;
        private double _sampleOffset, _velocity;

        private bool Swiping => _view._swipingClaudePermission == this;

        internal PermissionSwipeCard(ControlCenterView view, string id)
        {
            _view = view;
            _id = id;
            RenderTransform = _shift;
            _outcome = new TextBlock
            {
                FontFamily = view.ControlFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            PreviewMouseLeftButtonDown += (_, e) => Begin(e.GetPosition(view), e.Timestamp);
            PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !Move(e.GetPosition(view), e.Timestamp)) return;
                if (!IsMouseCaptured && !CaptureMouse())
                {
                    Cancel();
                    return;
                }
                e.Handled = true;
            };
            PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (Release(e.GetPosition(view), e.Timestamp)) e.Handled = true;
            };
            MouseLeave += (_, _) => { if (!Swiping) _origin = null; };
            LostMouseCapture += (_, e) => { if (e.OriginalSource == this && Swiping) Cancel(); };
            IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
        }

        internal void SetContent(Grid content)
        {
            _content = content;
            Child = new Grid { Children = { content, _outcome } };
        }

        internal void Begin(Point position, int timestamp)
        {
            if (!IsHitTestVisible || _view._swipingClaudePermission != null) return;
            _origin = position;
            _sampleTime = timestamp;
            _sampleOffset = _velocity = 0;
        }

        internal bool Move(Point position, int timestamp)
        {
            if (_origin is not { } origin) return false;
            Vector delta = position - origin;
            if (!Swiping)
            {
                if (delta.Length <= Slop) return false;
                if (Math.Abs(delta.X) <= Math.Abs(delta.Y))
                {
                    _origin = null;
                    return false;
                }
                _view._swipingClaudePermission = this;
            }

            UpdateSwipe(delta.X, timestamp);
            return true;
        }

        internal bool Release(Point position, int timestamp)
        {
            if (!Swiping || _origin is not { } origin)
            {
                _origin = null;
                return false;
            }

            double distance = position.X - origin.X;
            UpdateSwipe(distance, timestamp);
            EndCapture();
            if (Math.Abs(distance) >= Threshold)
            {
                bool allow = distance > 0;
                if (!_view.Settle(_id, () => _view._claude?.Decide(_id, allow ? ClaudeDecision.Allow : ClaudeDecision.Deny) == true,
                    allow ? 1 : -1)) SpringBack();
            }
            else SpringBack();
            return true;
        }

        private void UpdateSwipe(double distance, int timestamp)
        {
            double reach = Math.Abs(distance);
            double offset = Math.Sign(distance) * (Math.Min(reach, Threshold) * .6 + Math.Max(0, reach - Threshold) * .2);
            Motion.Stop(_shift, TranslateTransform.XProperty, AnimationConfig.ReduceMotion ? 0 : offset);
            int elapsed = unchecked(timestamp - _sampleTime);
            if (offset != _sampleOffset && elapsed > 0)
            {
                _velocity = Math.Clamp((offset - _sampleOffset) * 1000 / elapsed, -600, 600);
                _sampleOffset = offset;
                _sampleTime = timestamp;
            }
            else if (elapsed > 80) _velocity = 0;

            bool armed = reach >= Threshold;
            _outcome.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
            _content.Opacity = armed ? 0 : 1;
            if (!armed) return;
            bool allow = distance > 0;
            _outcome.Text = Loc.Get(allow ? "control.claude.allow" : "control.claude.deny");
            _outcome.Foreground = allow ? AllowInk : DenyInk;
        }

        private void EndCapture()
        {
            _origin = null;
            if (Swiping) _view._swipingClaudePermission = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void Cancel()
        {
            EndCapture();
            SpringBack();
        }

        private void SpringBack()
        {
            _outcome.Visibility = Visibility.Collapsed;
            _content.Opacity = 1;
            Motion.To(_shift, TranslateTransform.XProperty, 0, Motion.Press, velocity: _velocity);
            _view.BuildClaudeSection();
        }

        internal void Stop()
        {
            EndCapture();
            Motion.Stop(_shift, TranslateTransform.XProperty, 0);
            _outcome.Visibility = Visibility.Collapsed;
            _content.Opacity = 1;
        }
    }
}
