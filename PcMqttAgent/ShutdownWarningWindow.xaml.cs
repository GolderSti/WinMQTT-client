using System;
using System.Windows;
using System.Windows.Threading;

namespace PcMqttAgent;

public partial class ShutdownWarningWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _secondsLeft;
    private bool _isConfirmed;

    public ShutdownWarningWindow(string actionName)
    {
        InitializeComponent();
        
        _secondsLeft = App.Settings.ShutdownWarningSeconds;
        TxtMessage.Text = $"Получена команда: {actionName.ToUpper()}.\nВыполнить действие?";
        UpdateTimerText();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        _secondsLeft--;
        UpdateTimerText();

        if (_secondsLeft <= 0)
        {
            _timer.Stop();
            _isConfirmed = true; // Время вышло, выполняем действие
            this.DialogResult = true;
            this.Close();
        }
    }

    private void UpdateTimerText()
    {
        TxtTimer.Text = $"Автоматическое выполнение через: {_secondsLeft} сек.";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _isConfirmed = true;
        this.DialogResult = true;
        this.Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _isConfirmed = false;
        this.DialogResult = false;
        this.Close();
    }
}