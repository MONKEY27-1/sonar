using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Helpers;

namespace Soundboard.ViewModels.Auth;

/// <summary>
/// Drives every step of the sign-in experience — welcome, login, register, forgot password
/// (request + confirm), and email verification — as one view model with step-visibility flags,
/// the same pattern <see cref="Soundboard.ViewModels.FirstRunWizardViewModel"/> already uses for
/// its own multi-step flow. Each step is a separate UserControl under Views/Auth sharing this as
/// their DataContext.
/// </summary>
public partial class AuthViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private readonly ISessionService _sessionService;
    private readonly ILicenseService _licenseService;
    private readonly ISettingsService _settingsService;

    public AuthViewModel(
        IAuthenticationService authService,
        ISessionService sessionService,
        ILicenseService licenseService,
        ISettingsService settingsService)
    {
        _authService = authService;
        _sessionService = sessionService;
        _licenseService = licenseService;
        _settingsService = settingsService;

        _rememberMe = settingsService.Settings.Account.RememberMe;
    }

    /// <summary>Raised when the flow reaches a terminal, successful outcome (logged in,
    /// registered + verified, reset + logged in, or the user chose to continue offline) — the
    /// hosting window subscribes to this to close itself.</summary>
    public event EventHandler? Completed;

    // --- Step visibility (top-level "pages") ---
    [ObservableProperty] private bool _showWelcome = true;
    [ObservableProperty] private bool _showLogin;
    [ObservableProperty] private bool _showRegister;
    [ObservableProperty] private bool _showForgotPassword;
    [ObservableProperty] private bool _showEmailVerification;

    /// <summary>Sub-step within the Forgot Password page: false = enter email, true = enter
    /// code + new password. Not a top-level page on its own, so it isn't reset by <see cref="SetStep"/>.</summary>
    [ObservableProperty] private bool _showForgotConfirm;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _infoMessage = string.Empty;

    // --- Login ---
    [ObservableProperty] private string _loginEmailOrUsername = string.Empty;
    [ObservableProperty] private string _loginPassword = string.Empty;
    [ObservableProperty] private bool _rememberMe;

    // --- Register ---
    [ObservableProperty] private string _registerUsername = string.Empty;
    [ObservableProperty] private string _registerEmail = string.Empty;
    [ObservableProperty] private string _registerPassword = string.Empty;
    [ObservableProperty] private string _registerConfirmPassword = string.Empty;
    [ObservableProperty] private PasswordStrengthResult _registerPasswordStrength = PasswordStrengthResult.Empty;

    partial void OnRegisterPasswordChanged(string value) => RegisterPasswordStrength = PasswordStrengthEvaluator.Evaluate(value);

    // --- Forgot password ---
    [ObservableProperty] private string _forgotEmail = string.Empty;
    [ObservableProperty] private string _forgotCode = string.Empty;
    [ObservableProperty] private string _forgotNewPassword = string.Empty;
    [ObservableProperty] private string _forgotConfirmPassword = string.Empty;
    [ObservableProperty] private PasswordStrengthResult _forgotPasswordStrength = PasswordStrengthResult.Empty;

    partial void OnForgotNewPasswordChanged(string value) => ForgotPasswordStrength = PasswordStrengthEvaluator.Evaluate(value);

    // --- Email verification ---
    [ObservableProperty] private string _verifyEmail = string.Empty;
    [ObservableProperty] private string _verifyCode = string.Empty;

    // --- Navigation commands ---

    [RelayCommand]
    private void GoToLogin()
    {
        ClearMessages();
        SetStep(login: true);
    }

    [RelayCommand]
    private void GoToRegister()
    {
        ClearMessages();
        SetStep(register: true);
    }

    [RelayCommand]
    private void GoToWelcome()
    {
        ClearMessages();
        SetStep(welcome: true);
    }

    [RelayCommand]
    private void GoToForgotPassword()
    {
        ClearMessages();
        ForgotEmail = LoginEmailOrUsername.Contains('@') ? LoginEmailOrUsername : string.Empty;
        SetStep(forgotPassword: true);
    }

    [RelayCommand]
    private void ContinueOffline() => Completed?.Invoke(this, EventArgs.Empty);

    // --- Auth commands ---

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(LoginEmailOrUsername) || string.IsNullOrWhiteSpace(LoginPassword))
        {
            ErrorMessage = "Enter your email/username and password.";
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var result = await _authService.LoginAsync(LoginEmailOrUsername.Trim(), LoginPassword).ConfigureAwait(true);
            if (!result.Success || result.Value is null)
            {
                // If we at least know their email, send them straight to the verification step
                // instead of leaving them stuck on a bare error — this is the common "registered,
                // never verified, tried to log in later" path.
                if (result.ErrorKind == AuthErrorKind.EmailNotVerified && LoginEmailOrUsername.Contains('@'))
                {
                    VerifyEmail = LoginEmailOrUsername.Trim();
                    InfoMessage = "You'll need to verify your email before logging in.";
                    SetStep(emailVerification: true);
                    return;
                }

                ErrorMessage = result.ErrorMessage ?? "Couldn't log in.";
                return;
            }

            await CompleteSignInAsync(result.Value, RememberMe).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsBusy) return;

        if (!InputValidators.IsValidUsername(RegisterUsername))
        {
            ErrorMessage = "Username must be 3-20 characters — letters, numbers, underscore only.";
            return;
        }

        if (!InputValidators.IsValidEmail(RegisterEmail))
        {
            ErrorMessage = "Enter a valid email address.";
            return;
        }

        if (!InputValidators.IsValidPassword(RegisterPassword))
        {
            ErrorMessage = InputValidators.PasswordRequirementsText;
            return;
        }

        if (RegisterPassword != RegisterConfirmPassword)
        {
            ErrorMessage = "Passwords don't match.";
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var result = await _authService.RegisterAsync(RegisterUsername.Trim(), RegisterEmail.Trim(), RegisterPassword).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.ErrorMessage ?? "Couldn't create the account.";
                return;
            }

            VerifyEmail = RegisterEmail.Trim();
            InfoMessage = $"We sent a verification code to {VerifyEmail}.";
            SetStep(emailVerification: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyEmailCodeAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(VerifyCode))
        {
            ErrorMessage = "Enter the code from your email.";
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var result = await _authService.VerifyEmailAsync(VerifyEmail, VerifyCode.Trim()).ConfigureAwait(true);
            if (!result.Success || result.Value is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Couldn't verify that code.";
                return;
            }

            // Verifying returns a live session — go straight into the signed-in state rather
            // than making them log in again right after registering.
            await CompleteSignInAsync(result.Value, rememberMe: true).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResendVerificationAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(VerifyEmail)) return;

        IsBusy = true;
        ClearMessages();
        try
        {
            var result = await _authService.ResendVerificationEmailAsync(VerifyEmail).ConfigureAwait(true);
            InfoMessage = result.Success ? "Verification code resent." : result.ErrorMessage ?? "Couldn't resend the code.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RequestPasswordResetAsync()
    {
        if (IsBusy) return;

        if (!InputValidators.IsValidEmail(ForgotEmail))
        {
            ErrorMessage = "Enter a valid email address.";
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var result = await _authService.RequestPasswordResetAsync(ForgotEmail.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.ErrorMessage ?? "Couldn't request a password reset.";
                return;
            }

            InfoMessage = $"We sent a reset code to {ForgotEmail}.";
            ShowForgotConfirm = true; // sub-step within the same Forgot Password page
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmPasswordResetAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(ForgotCode))
        {
            ErrorMessage = "Enter the code from your email.";
            return;
        }

        if (!InputValidators.IsValidPassword(ForgotNewPassword))
        {
            ErrorMessage = InputValidators.PasswordRequirementsText;
            return;
        }

        if (ForgotNewPassword != ForgotConfirmPassword)
        {
            ErrorMessage = "Passwords don't match.";
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var result = await _authService.ConfirmPasswordResetAsync(ForgotEmail.Trim(), ForgotCode.Trim(), ForgotNewPassword).ConfigureAwait(true);
            if (!result.Success || result.Value is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Couldn't reset your password.";
                return;
            }

            await CompleteSignInAsync(result.Value, rememberMe: true).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompleteSignInAsync(AuthSession session, bool rememberMe)
    {
        var profileResult = await _authService.GetProfileAsync(session).ConfigureAwait(true);
        if (!profileResult.Success || profileResult.Value is null)
        {
            // Surface the real reason (e.g. "This account has been suspended...") rather than a
            // generic fallback — GetProfileAsync is also where account suspension is enforced.
            ErrorMessage = profileResult.ErrorMessage ?? "Signed in, but couldn't load your profile. Please try again.";
            return;
        }

        _settingsService.Settings.Account.RememberMe = rememberMe;
        await _settingsService.SaveAsync().ConfigureAwait(true);

        await _sessionService.SetSessionAsync(session, profileResult.Value, rememberMe).ConfigureAwait(true);
        _licenseService.UpdateFromProfile(profileResult.Value);

        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
    }

    private void SetStep(bool welcome = false, bool login = false, bool register = false,
        bool forgotPassword = false, bool emailVerification = false)
    {
        ShowWelcome = welcome;
        ShowLogin = login;
        ShowRegister = register;
        ShowForgotPassword = forgotPassword;
        ShowForgotConfirm = false; // always start a fresh visit to Forgot Password on the email step
        ShowEmailVerification = emailVerification;
    }
}
