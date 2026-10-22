// SPDX-FileCopyrightText: 2026 Froststrap
//
// SPDX-License-Identifier: MPL-2.0

using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Froststrap.Integrations;
using Froststrap.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
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

        public ICommand ToggleConfigCommand => new RelayCommand(() => IsConfigOpen = !IsConfigOpen);
        public ICommand JoinPartyCommand => new AsyncRelayCommand(JoinPartyAsync);

        private static PartyHpOverlayWindow? _instance;
        private static ActivityWatcher? _monitoredActivityWatcher;

        public static void InitializeWatcher(ActivityWatcher activityWatcher)
        {
            _monitoredActivityWatcher = activityWatcher;
            _monitoredActivityWatcher.OnGameJoin += (s, e) =>
            {
                // Deepwoken Place ID: 4111023553
                if (_monitoredActivityWatcher.Data.PlaceId == 4111023553)
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_instance == null && App.Settings.Prop.EnablePartyHpSync)
                        {
                            _instance = new PartyHpOverlayWindow();
                            _instance.Show();
                            _ = PartyHpManager.StartAsync();
                        }
                    });
                }
            };

            _monitoredActivityWatcher.OnGameLeave += (s, e) =>
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_instance != null)
                    {
                        PartyHpManager.Stop();
                        _instance.Close();
                        _instance = null;
                    }
                });
            };
        }

        public PartyHpOverlayWindow()
        {
            InitializeComponent();
            DataContext = this;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new Avalonia.PixelPoint(20, 150);

            PartyHpManager.PartyUpdated += UpdatePartyList;
            Closed += (s, e) =>
            {
                PartyHpManager.PartyUpdated -= UpdatePartyList;
                _instance = null;
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async Task JoinPartyAsync()
        {
            App.Settings.Prop.PartyRoomCode = RoomCodeInput;
            App.Settings.Save();

            IsConfigOpen = false;
            await PartyHpManager.StartAsync();
        }

        private void UpdatePartyList(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
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
