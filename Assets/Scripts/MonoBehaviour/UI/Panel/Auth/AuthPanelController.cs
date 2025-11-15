using TMPro;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;
using UnityEngine.UI;


// 登录 / 注册 UI 控制器
public class AuthPanelController : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] private GameObject _loginPanel;
    [SerializeField] private GameObject _registerPanel;
    [SerializeField] private GameObject _mainMenuPanel;
    [SerializeField] private GameObject _authPanel;

    [Header("Auth UI")]
    [SerializeField] private Button _showRegisterPanelButton;
    [SerializeField] private Button _showLoginPanelButton;


    [Header("Login UI")]
    [SerializeField] private TMP_InputField _loginUserNameInput;
    [SerializeField] private TMP_InputField _loginPasswordInput;
    [SerializeField] private Button _loginButton;
    [SerializeField] private Button _returnFromLoginButton;

    [Header("Register UI")]
    [SerializeField] private TMP_InputField _registerUserNameInput;
    [SerializeField] private TMP_InputField _registerPasswordInput;
    [SerializeField] private Button _registerInButton;
    [SerializeField] private Button _returnFromRegistrationButton;

    [Header("Feedback")]
    [SerializeField] private FloatingMessageView _floatingMessageView;

    private void Awake()
    {
        UserAccountStorage.InitializeIfNeeded();
        _registerPanel?.SetActive(false);
        _mainMenuPanel?.SetActive(false);
        _loginPanel?.SetActive(false);

        if (_showRegisterPanelButton != null)
        {
            _showRegisterPanelButton.onClick.AddListener(ShowRegisterPanel);
        }

        if (_showLoginPanelButton != null)
        {
            _showLoginPanelButton.onClick.AddListener(ShowLoginPanel);
        }

        if (_loginButton != null)
        {
            _loginButton.onClick.AddListener(OnLoginClicked);
        }

        if (_returnFromRegistrationButton != null)
        {
            _returnFromRegistrationButton.onClick.AddListener(ReturnFromRegisterOrLoginPanel);
        }

        if (_registerInButton != null)
        {
            _registerInButton.onClick.AddListener(OnRegisterClicked);
        }

        if (_returnFromLoginButton != null)
        {
            _returnFromLoginButton.onClick.AddListener(ReturnFromRegisterOrLoginPanel);
        }

        if (_floatingMessageView != null)
        {
            _floatingMessageView.MessageText.text = string.Empty;
        }
    }

    private void ShowLoginPanel()
    {
        if (_loginPanel != null)
        {
            _loginPanel.SetActive(true);
        }

        if (_registerPanel != null)
        {
            _registerPanel.SetActive(false);
        }

        if (_floatingMessageView != null)
        {
           _floatingMessageView.MessageText.text = string.Empty;
        }
    }

    private void ShowRegisterPanel()
    {
        if (_loginPanel != null)
        {
            _loginPanel.SetActive(false);
        }

        if (_registerPanel != null)
        {
            _registerPanel.SetActive(true);
        }

        if (_floatingMessageView != null)
        {
           _floatingMessageView.MessageText.text = string.Empty;
        }
    }

    private void OnLoginClicked()
    {
        string userName = _loginUserNameInput != null ? _loginUserNameInput.text : string.Empty;
        string password = _loginPasswordInput != null ? _loginPasswordInput.text : string.Empty;

        if (UserAccountStorage.TryLogin(userName, password, out var errorMessage))
        {
            PlayerSession.SetLoggedInUser(userName);

            if (_floatingMessageView != null)
            {
                _floatingMessageView.ShowMessage("Login successful!");
            }

            EnterMainMenuPanel();
        }
        else
        {
            if (_floatingMessageView != null)
            {
                _floatingMessageView.ShowMessage(errorMessage);
            }
        }
    }

    private void OnRegisterClicked()
    {
        string userName = _registerUserNameInput != null ? _registerUserNameInput.text : string.Empty;
        string password = _registerPasswordInput != null ? _registerPasswordInput.text : string.Empty;
        
        if (UserAccountStorage.TryRegister(userName, password, out var errorMessage))
        {
            if (_floatingMessageView != null)
            {
                _floatingMessageView.ShowMessage("Registration successful!");
            }

            EnterMainMenuPanel();

            if (_loginUserNameInput != null)
            {
                _loginUserNameInput.text = userName;
            }
        }
        else
        {
            if (_floatingMessageView != null)
            {
                _floatingMessageView.ShowMessage(errorMessage);
            }
        }
    }

    private void EnterMainMenuPanel()
    {
        _authPanel?.SetActive(false);
        _mainMenuPanel?.SetActive(true);
    }

    private void ReturnFromRegisterOrLoginPanel()
    {
        _registerPanel?.SetActive(false);
        _loginPanel?.SetActive(false);
    }
}
