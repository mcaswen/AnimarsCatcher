using TMPro;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Account;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 管理登录、注册和主菜单面板之间的认证流程
    /// 输入校验和账号持久化委托给 UserAccountStorage
    /// </summary>
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

        // 切换到登录表单并清除上一次反馈
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

        // 切换到注册表单并清除上一次反馈
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

        // 验证凭据，成功后建立进程内玩家会话
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

        // 创建本地账号，并把用户名填入登录表单
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

        // 认证流程完成后隐藏认证根面板并进入主菜单
        private void EnterMainMenuPanel()
        {
            _authPanel?.SetActive(false);
            _mainMenuPanel?.SetActive(true);
        }

        // 返回认证入口并关闭当前表单
        private void ReturnFromRegisterOrLoginPanel()
        {
            _registerPanel?.SetActive(false);
            _loginPanel?.SetActive(false);
        }
    }
}
