// SPDX-FileCopyrightText: 2026 Froststrap
//
// SPDX-License-Identifier: MPL-2.0

using Avalonia.Controls;
using Avalonia.Data.Converters;
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

        public ICommand ToggleConfigCommand => new RelayCommand(() => IsConfigOpen = !IsConfigOpen);
        public ICommand JoinPartyCommand => new AsyncRelayCommand(JoinPartyAsync);

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
            Closed += (s, e) =>
            {
                PartyHpManager.PartyUpdated -= UpdatePartyList;
                PartyHpManager.ConnectionFailed -= OnConnectionFailed;
                _instance = null;
            };
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

        private async Task JoinPartyAsync()
        {
            App.Settings.Prop.PartyRoomCode = RoomCodeInput;
            App.Settings.Prop.PartyUsername = UsernameInput;
            if (long.TryParse(UserIdInput, out long uid))
                App.Settings.Prop.PartyUserId = uid;
            else
                App.Settings.Prop.PartyUserId = 0;
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
