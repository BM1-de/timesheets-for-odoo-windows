// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.App.Views;

public sealed partial class LoginView : UserControl, INotifyPropertyChanged
{
    private AppModel? model;
    private string server = "";
    private string apiKey = "";
    private string loginName = "";
    private string database = "";
    private bool didPrefill;

    public LoginView()
    {
        InitializeComponent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void Initialize(AppModel appModel)
    {
        model = appModel;
        model.PropertyChanged += (_, _) =>
        {
            if (model.Screen == Screen.Login) Prefill();
            Changed();
        };
        Prefill();
    }

    /// <summary>Angaben der letzten Anmeldung; eine vorausgefüllte Datenbank steht immer da, damit sie nicht unbemerkt mitgeht.</summary>
    private void Prefill()
    {
        if (didPrefill || model is null || model.Screen != Screen.Login) return;
        didPrefill = true;
        Result = null;
        if (model.Preferences.LoginHints is { } hints)
        {
            server = hints.Server;
            if (hints.Login is { } login)
            {
                loginName = login;
                ShowsLogin = true;
            }
            if (hints.Database is { } hintDatabase)
            {
                database = hintDatabase;
                ShowsDatabase = true;
            }
        }
        Changed();
    }

    public void FocusFirstField()
    {
        if (IsDone) DoneButton.Focus(FocusState.Programmatic);
        else if (server.Length == 0) ServerBox.Focus(FocusState.Programmatic);
        else KeyBox.Focus(FocusState.Programmatic);
    }

    public string Server
    {
        get => server;
        set
        {
            server = value;
            Changed();
        }
    }

    public string LoginName
    {
        get => loginName;
        set
        {
            loginName = value;
            Changed();
        }
    }

    public string Database
    {
        get => database;
        set
        {
            database = value;
            Changed();
        }
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        apiKey = KeyBox.Password;
        Changed();
    }

    public bool ShowsLogin { get; private set; }

    public bool ShowsDatabase { get; private set; }

    public bool IsConnecting { get; private set; }

    public string? ErrorText { get; private set; }

    public Capabilities? Result { get; private set; }

    public bool IsDone => Result is not null;

    public bool CanCancel => model?.Session is not null && !IsConnecting;

    public bool CanConnect =>
        !IsConnecting && server.Trim().Length > 0 && apiKey.Trim().Length > 0
        && (!ShowsLogin || loginName.Trim().Length > 0) && (!ShowsDatabase || database.Trim().Length > 0);

    public string HeadTitle => IsDone ? Loc.T("Verbunden") : Loc.T("Mit Odoo verbinden");

    public string HeadText => Result is { } found && model is not null
        ? Loc.T("Angemeldet als %@ bei %@.", found.UserName, model.ServerDisplay)
        : Loc.T("Einmal anmelden, danach erfasst du Zeiten direkt aus dem Infobereich der Taskleiste.");

    private FeatureText Timesheets => FeatureText.Timesheets(Result);
    private FeatureText Attendance => FeatureText.Attendance(Result);

    public string TimesheetsGlyph => Timesheets.Glyph;
    public Brush TimesheetsBrush => Timesheets.Brush;
    public string TimesheetsText => Timesheets.IsAvailable ? Loc.T("eingeschaltet") : Timesheets.Text;
    public string AttendanceGlyph => Attendance.Glyph;
    public Brush AttendanceBrush => Attendance.Brush;
    public string AttendanceText => Attendance.IsAvailable ? Loc.T("eingeschaltet") : Attendance.Text;

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void Field_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !CanConnect) return;
        e.Handled = true;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (model is null || !CanConnect) return;
        IsConnecting = true;
        ErrorText = null;
        Changed();
        try
        {
            Result = await model.SignInAsync(server, apiKey, ShowsLogin ? loginName : null,
                                             database.Trim().Length == 0 ? null : database);
            apiKey = "";
            KeyBox.Password = "";
            DispatcherQueue.TryEnqueue(() => DoneButton.Focus(FocusState.Programmatic));
        }
        catch (OdooException error) when (error.Kind == OdooErrorKind.LoginRequired)
        {
            ShowsLogin = true;
            ErrorText = error.Message;
            DispatcherQueue.TryEnqueue(() => LoginBox.Focus(FocusState.Programmatic));
        }
        catch (OdooException error) when (error.Kind == OdooErrorKind.DatabaseRequired)
        {
            ShowsDatabase = true;
            ErrorText = error.Message;
            DispatcherQueue.TryEnqueue(() => DatabaseBox.Focus(FocusState.Programmatic));
        }
        catch (Exception error)
        {
            ErrorText = error.Message;
        }
        finally
        {
            IsConnecting = false;
            Changed();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => model?.CancelSignIn();

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        didPrefill = false;
        model?.FinishSignIn();
    }
}

/// <summary>Was die App beim Verbinden in Odoo gefunden hat, mit Zeichen und Farbe.</summary>
public sealed record FeatureText(string Glyph, Brush Brush, string Text, bool IsAvailable)
{
    private const string Check = "";
    private const string Info = "";
    private const string Warning = "";
    private const string Unknown = "";

    public static FeatureText Timesheets(Capabilities? capabilities) => capabilities?.Timesheets switch
    {
        null => NotChecked,
        FeatureStatus.Available => new FeatureText(Check, Brushes.Success, Loc.T("Odoo-App „Zeiterfassung“ gefunden"), true),
        FeatureStatus.NotInstalled => Problem(Loc.T("Die Odoo-App „Zeiterfassung“ ist in dieser Datenbank nicht installiert.")),
        FeatureStatus.NoEmployee => Problem(Loc.T("Dein Odoo-Benutzer ist mit keinem Mitarbeiter verknüpft.")),
        _ => Problem(Loc.T("Kein Zugriff auf Zeiteinträge.")),
    };

    public static FeatureText Attendance(Capabilities? capabilities) => capabilities switch
    {
        null => NotChecked,
        { Attendance: FeatureStatus.Available, CanStamp: true } =>
            new FeatureText(Check, Brushes.Success, Loc.T("Odoo-App „Anwesenheiten“ gefunden"), true),
        { Attendance: FeatureStatus.Available } => new FeatureText(Info, Brushes.Accent,
            Loc.T("Gefunden. Stempeln und Korrigieren braucht in Odoo das Recht „Sachbearbeiter: Alle Anwesenheiten verwalten“, bis dahin nur Anzeige."),
            true),
        { Attendance: FeatureStatus.NotInstalled } =>
            Problem(Loc.T("Die Odoo-App „Anwesenheiten“ ist nicht installiert. Nach der Installation auf „Erneut prüfen“ klicken.")),
        { Attendance: FeatureStatus.NoEmployee } =>
            Problem(Loc.T("Dein Odoo-Benutzer ist mit keinem Mitarbeiter verknüpft. Ohne Verknüpfung speichert Odoo keine Anwesenheit.")),
        _ => Problem(Loc.T("Kein Zugriff auf Anwesenheiten.")),
    };

    private static FeatureText NotChecked => new(Unknown, Brushes.Secondary, Loc.T("Wird nach der Anmeldung geprüft."), false);

    private static FeatureText Problem(string text) => new(Warning, Brushes.Caution, text, false);
}
