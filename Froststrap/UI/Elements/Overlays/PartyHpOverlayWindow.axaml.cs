// SPDX-FileCopyrightText: 2026 Froststrap
//
// SPDX-License-Identifier: MPL-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Froststrap.Integrations;
using Froststrap.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Froststrap.UI.Elements.Overlays
{
    internal class FirstLetterConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s && !string.IsNullOrEmpty(s))
                return s[..1].ToUpper(culture);
            return "?";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    internal class UrlToBitmapConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string url && !string.IsNullOrEmpty(url))
            {
                try
                {
                    return new Bitmap(App.HttpClient.GetStreamAsync(new Uri(url)).Result);
                }
                catch { }
            }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    internal partial class PartyHpOverlayWindow : Window, System.ComponentModel.INotifyPropertyChanged
    {
        public ObservableCollection<PartyMemberHp> Members { get; } = [];

        private bool _isConfigOpen;
        public bool IsConfigOpen
        {
            get => _isConfigOpen;
            set => SetProperty(ref _isConfigOpen, value);
        }

        private string _roomCodeInput = App.Settings.Prop.PartyRoomCode;
        public string RoomCodeInput
        {
            get => _roomCodeInput;
            set => SetProperty(ref _roomCodeInput, value);
        }

        private string _usernameInput = App.Settings.Prop.PartyUsername;
        public string UsernameInput
        {
            get => _usernameInput;
            set => SetProperty(ref _usernameInput, value);
        }

        private string _userIdInput = App.Settings.Prop.PartyUserId == 0 ? "" : App.Settings.Prop.PartyUserId.ToString(CultureInfo.InvariantCulture);
        public string UserIdInput
        {
            get => _userIdInput;
            set => SetProperty(ref _userIdInput, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _useScreenCapture = App.Settings.Prop.PartyHpUseScreenCapture;
        public bool UseScreenCapture
        {
            get => _useScreenCapture;
            set => SetProperty(ref _useScreenCapture, value);
        }

        private string _barXInput = App.Settings.Prop.PartyHpBarX.ToString(CultureInfo.InvariantCulture);
        public string BarXInput { get => _barXInput; set => SetProperty(ref _barXInput, value); }

        private string _barYInput = App.Settings.Prop.PartyHpBarY.ToString(CultureInfo.InvariantCulture);
        public string BarYInput { get => _barYInput; set => SetProperty(ref _barYInput, value); }

        private string _barWidthInput = App.Settings.Prop.PartyHpBarWidth.ToString(CultureInfo.InvariantCulture);
        public string BarWidthInput { get => _barWidthInput; set => SetProperty(ref _barWidthInput, value); }

        private string _barHeightInput = App.Settings.Prop.PartyHpBarHeight.ToString(CultureInfo.InvariantCulture);
        public string BarHeightInput { get => _barHeightInput; set => SetProperty(ref _barHeightInput, value); }

        private string _healthStatus = "Waiting for health reading…";
        public string HealthStatus
        {
            get => _healthStatus;
            set => SetProperty(ref _healthStatus, value);
        }

        public ICommand ToggleConfigCommand => new RelayCommand(() => IsConfigOpen = !IsConfigOpen);
        public ICommand JoinPartyCommand => new AsyncRelayCommand(JoinPartyAsync);
        public ICommand PickRegionCommand => new AsyncRelayCommand(PickRegionAsync);

        private static PartyHpOverlayWindow? _instance;
        private static ActivityWatcher? _monitoredActivityWatcher;
        private static CancellationTokenSource? _monitorCts;

        public static void InitializeWatcher(ActivityWatcher activityWatcher)
        {
            _monitoredActivityWatcher = activityWatcher;
            _monitorCts?.Cancel();
            _monitorCts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                while (!_monitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        long placeId = _monitoredActivityWatcher?.Data?.PlaceId ?? 0;
                        bool isDeepwoken = placeId == 4111023553;
                        bool alwaysShow = App.Settings.Prop.PartyHpAlwaysShow;
                        bool shouldShow = App.Settings.Prop.EnablePartyHpSync && (alwaysShow || isDeepwoken);

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (shouldShow)
                            {
                                if (_instance == null)
                                {
                                    App.Logger.Info("[PartyHP] Deepwoken detected (or always show), opening overlay.");
                                    _instance = new PartyHpOverlayWindow();
                                    _instance.Show();
                                    _ = PartyHpManager.StartAsync();
                                }
                                else if (!_instance.IsVisible)
                                {
                                    _instance.Show();
                                }
                            }
                            else
                            {
                                if (_instance != null && !isDeepwoken && !alwaysShow)
                                {
                                    App.Logger.Info("[PartyHP] Left Deepwoken, closing overlay.");
                                    PartyHpManager.Stop();
                                    _instance.Close();
                                    _instance = null;
                                }
                            }
                        });
                    }
                    catch { }

                    await Task.Delay(1500, _monitorCts.Token);
                }
            }, _monitorCts.Token);
        }

        public PartyHpOverlayWindow()
        {
            InitializeComponent();
            DataContext = this;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new Avalonia.PixelPoint(20, 150);

            PartyHpManager.PartyUpdated += UpdatePartyList;
            PartyHpManager.ConnectionFailed += OnConnectionFailed;
            PartyHpManager.HealthSampleChanged += OnHealthSample;
            Closed += (s, e) =>
            {
                PartyHpManager.PartyUpdated -= UpdatePartyList;
                PartyHpManager.ConnectionFailed -= OnConnectionFailed;
                PartyHpManager.HealthSampleChanged -= OnHealthSample;
                _instance = null;
            };
        }

        private void OnHealthSample(object? sender, Froststrap.Integrations.Health.HealthSample sample)
        {
            string text = sample.Percent is double pct
                ? $"Health {pct:0.#}%" +
                  (sample.WitherPercent is double w and > 0 ? $"  · Wither {w:0.#}%" : "") +
                  (sample.TemporaryPercent is double t and > 0 ? $"  · Temp {t:0.#}%" : "")
                : sample.Status;

            Dispatcher.UIThread.InvokeAsync(() => HealthStatus = text);
        }

        private void OnConnectionFailed(object? sender, string reason)
        {
            string friendly = reason switch
            {
                "name_taken" => "That name is already in use in this party.",
                "party_full" => "Party is full (12/12).",
                "name_too_long" or "player_name_too_long" => "Your Roblox name is too long for this server.",
                "rate_limit_exceeded" => "Disconnected: too many messages sent.",
                "party_full_or_name_taken" => "Party is full or that name is already taken.",
                _ when reason.Contains("No connection", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
                    => "Can't reach party server, retrying…",
                _ => $"Connection error: {reason}"
            };

            Dispatcher.UIThread.InvokeAsync(() => StatusText = friendly);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async Task PickRegionAsync()
        {
            // Enabling screen capture is implicit if the user pressed Pick.
            UseScreenCapture = true;
            App.Settings.Prop.PartyHpUseScreenCapture = true;

            // Hide this window during the pick so it doesn't cover the HP bar.
            bool wasVisible = IsVisible;
            try { Hide(); } catch { }

            try
            {
                var picker = new Window(); // transient owner for Screens.All lookup
                picker.Show();
                picker.Hide();
                var region = await PartyHpRegionPicker.PickAsync(picker);
                picker.Close();

                if (region != null)
                {
                    App.Settings.Prop.PartyHpBarX = region.X;
                    App.Settings.Prop.PartyHpBarY = region.Y;
                    App.Settings.Prop.PartyHpBarWidth = region.Width;
                    App.Settings.Prop.PartyHpBarHeight = region.Height;
                    App.Settings.Save();

                    BarXInput = region.X.ToString(CultureInfo.InvariantCulture);
                    BarYInput = region.Y.ToString(CultureInfo.InvariantCulture);
                    BarWidthInput = region.Width.ToString(CultureInfo.InvariantCulture);
                    BarHeightInput = region.Height.ToString(CultureInfo.InvariantCulture);

                    App.Logger.Info($"[PartyHP] Region picked: {region.X},{region.Y} {region.Width}x{region.Height}. Restarting monitor.");

                    // Apply immediately: restart party connection so monitor picks up the new region.
                    await PartyHpManager.StartAsync();
                }
            }
            catch (Exception ex)
            {
                App.Logger.Error($"[PartyHP] Region picker error: {ex.Message}");
            }
            finally
            {
                if (wasVisible) { try { Show(); } catch { } }
            }
        }

        private async Task JoinPartyAsync()
        {
            App.Settings.Prop.PartyRoomCode = RoomCodeInput;
            App.Settings.Prop.PartyUsername = UsernameInput;
            if (long.TryParse(UserIdInput, out long uid))
                App.Settings.Prop.PartyUserId = uid;
            else
                App.Settings.Prop.PartyUserId = 0;

            App.Settings.Prop.PartyHpUseScreenCapture = UseScreenCapture;
            if (int.TryParse(BarXInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bx))
                App.Settings.Prop.PartyHpBarX = bx;
            if (int.TryParse(BarYInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int by))
                App.Settings.Prop.PartyHpBarY = by;
            if (int.TryParse(BarWidthInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bw))
                App.Settings.Prop.PartyHpBarWidth = bw;
            if (int.TryParse(BarHeightInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bh))
                App.Settings.Prop.PartyHpBarHeight = bh;

            App.Settings.Save();

            IsConfigOpen = false;
            StatusText = "";
            await PartyHpManager.StartAsync();
        }

        private void UpdatePartyList(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (PartyHpManager.IsConnected && !string.IsNullOrEmpty(StatusText))
                    StatusText = "";

                Members.Clear();
                foreach (var member in PartyHpManager.PartyMembers.Values)
                {
                    Members.Add(member);
                }
            });
        }

        public new event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
