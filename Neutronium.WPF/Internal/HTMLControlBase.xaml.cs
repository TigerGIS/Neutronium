﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Neutronium.Core;
using Neutronium.Core.Exceptions;
using Neutronium.Core.Infra.VM;
using Neutronium.Core.JavascriptFramework;
using Neutronium.Core.Navigation;
using Neutronium.Core.WebBrowserEngine.Control;
using Neutronium.Core.WebBrowserEngine.Window;
using Neutronium.WPF.Internal.DebugTools;
using Neutronium.WPF.Utils;
using Microsoft.Win32;
using Neutronium.Core.Extension;
using Neutronium.Core.WebBrowserEngine.JavascriptObject;
using Neutronium.WPF.Internal.ViewModel;

namespace Neutronium.WPF.Internal
{
    public abstract partial class HTMLControlBase : IWebViewLifeCycleManager, IDisposable
    {
        private UserControl _DebugControl;
        private readonly DebugInformation _DebugInformation;
        private IWPFWebWindowFactory _WpfWebWindowFactory;
        private IWebSessionLogger _WebSessionLogger;
        private readonly IUrlSolver _UrlSolver;
        private IJavascriptFrameworkManager _Injector;
        private string _SaveDirectory;
        private bool _LoadingAbout = false;
        private HTMLSimpleWindow _VmDebugWindow;
        private DoubleBrowserNavigator _WpfDoubleBrowserNavigator;
        private Window Window => Window.GetWindow(this);
        private DoubleBrowserNavigator WpfDoubleBrowserNavigator
        {
            get
            {
                Init();
                return _WpfDoubleBrowserNavigator;
            }
        }

        public bool DebugContext => IsDebug;
        public Uri Source => _WpfDoubleBrowserNavigator?.Url;
        public IWPFWebWindow WPFWebWindow => (WpfDoubleBrowserNavigator.WebControl as WPFHTMLWindowProvider)?.WPFWebWindow;

        public bool IsDebug
        {
            get => (bool)GetValue(IsDebugProperty);
            set => SetValue(IsDebugProperty, value);
        }

        public static readonly DependencyProperty IsDebugProperty = DependencyProperty.Register(nameof(IsDebug), typeof(bool), typeof(HTMLControlBase), new PropertyMetadata(false, DebugChanged));

        private static void DebugChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as HTMLControlBase;
            control.DebugChanged((bool)e.NewValue);
        }

        public bool IsHTMLLoaded
        {
            get => (bool)GetValue(IsHTMLLoadedProperty);
            private set => SetValue(IsHTMLLoadedProperty, value);
        }

        public static readonly DependencyProperty IsHTMLLoadedProperty =
            DependencyProperty.Register(nameof(IsHTMLLoaded), typeof(bool), typeof(HTMLControlBase), new PropertyMetadata(false));

        public string HTMLEngine
        {
            get => (string)GetValue(HTMLEngineProperty);
            set => SetValue(HTMLEngineProperty, value);
        }

        public static readonly DependencyProperty HTMLEngineProperty =
            DependencyProperty.Register(nameof(HTMLEngine), typeof(string), typeof(HTMLControlBase), new PropertyMetadata(string.Empty));

        public string JavascriptUIEngine
        {
            get => (string)GetValue(JavascriptUIEngineProperty);
            set => SetValue(JavascriptUIEngineProperty, value);
        }

        public static readonly DependencyProperty JavascriptUIEngineProperty =
            DependencyProperty.Register(nameof(JavascriptUIEngine), typeof(string), typeof(HTMLControlBase), new PropertyMetadata(string.Empty));

        private bool _UseINavigable = true;
        public bool UseINavigable
        {
            get => _UseINavigable;
            set
            {
                _UseINavigable = value;
                if (_WpfDoubleBrowserNavigator != null)
                {
                    _WpfDoubleBrowserNavigator.UseINavigable = _UseINavigable;
                }
            }
        }

        public abstract string UniqueName { get; }

        public event EventHandler<NavigationEvent> OnNavigate;
        public event EventHandler<FirstLoadEvent> OnFirstLoad;
        public event EventHandler<DisplayEvent> OnDisplay;

        protected HTMLControlBase(IUrlSolver urlSolver)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            _UrlSolver = urlSolver;

            _DebugInformation = new DebugInformation
            {
                DebugWindow = new BasicRelayCommand(ShowDebugWindow),
                DebugBrowser = new BasicRelayCommand(OpenDebugBrowser),
                ShowInfo = new BasicRelayCommand(DoShowInfo),
                SaveVm = new BasicRelayCommand(DoSaveVm),
                IsDebuggingVm = false,
                NeutroniumWPFVersion = VersionHelper.GetVersion(this).GetDisplayName(),
                ComponentName = this.GetType().Name
            };

            InitializeComponent();

            this.Initialized += HTMLControlBase_Initialized;
        }

        private void DoSaveVm()
        {
            var binding = _WpfDoubleBrowserNavigator?.Binding?.JsBrideRootObject;
            if (binding == null)
                return;

            var savefile = new SaveFileDialog
            {
                FileName = "vm.cjson",
                InitialDirectory = ComputeProposedDirectory()
            };

            if (savefile.ShowDialog() != true)
                return;

            var fileName = savefile.FileName;
            _SaveDirectory = Path.GetDirectoryName(fileName);
            var content = binding.AsCircularVersionedJson();
            File.WriteAllLines(fileName, new[] { content });
        }

        private string ComputeProposedDirectory()
        {
            var path = _WpfDoubleBrowserNavigator?.HTMLWindow?.Url?.AbsolutePath;
            if (path == null)
                return _SaveDirectory;

            var directory = Path.GetDirectoryName(path); ;
            var transformed = directory.Replace(@"\bin\Debug", string.Empty);
            transformed = transformed.Replace(@"\bin\Release", string.Empty);

            if (transformed.Length == directory.Length)
                return _SaveDirectory;

            var dataFolder = Path.Combine(Path.GetDirectoryName(transformed), "data");
            return Directory.Exists(dataFolder) ? dataFolder : _SaveDirectory;
        }

        private void DebugChanged(bool isDebug)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            if ((_WpfWebWindowFactory == null) || (_WpfWebWindowFactory.IsModern))
                return;

            if (isDebug)
            {
                SetUpDebugTool();
            }
            else
            {
                MainGrid.Children.Remove(_DebugControl);
                _DebugControl = null;
            }
        }

        private void SetUpDebugTool()
        {
            if (_DebugControl != null)
                return;

            if (_WpfWebWindowFactory.IsModern)
                return;

            var windoInfo = HTMLEngineFactory.Engine.ResolveToolbar();
            if (windoInfo != null)
            {
                _DebugControl = new DebugControlNeutronium(windoInfo.AbsolutePath, windoInfo.Framework.Name)
                {
                    Height = windoInfo.Height
                };
            }
            else
            {
                _DebugControl = new DebugControl();
            }
            _DebugControl.DataContext = _DebugInformation;
            Grid.SetRow(_DebugControl, 1);
            MainGrid.Children.Add(_DebugControl);
        }

        private void HTMLControlBase_Initialized(object sender, EventArgs e)
        {
            Init();
        }

        private void Init()
        {
            if (_WpfWebWindowFactory != null)
                return;

            var engine = HTMLEngineFactory.Engine;
            _WpfWebWindowFactory = engine.ResolveJavaScriptEngine(HTMLEngine);

            if (_WpfWebWindowFactory == null)
                throw ExceptionHelper.Get($"Not able to find WebEngine {HTMLEngine}");

            _Injector = engine.ResolveJavaScriptFramework(JavascriptUIEngine);

            if (_Injector == null)
                throw ExceptionHelper.Get($"Not able to find JavascriptUIEngine {JavascriptUIEngine}. Please register the correspoding Javascript UIEngine.");

            _WpfDoubleBrowserNavigator = GetDoubleBrowserNavigator();

            WebSessionLogger = WebSessionLogger ?? engine.WebSessionLogger;

            if (IsDebug)
                SetUpDebugTool();
        }

        private DoubleBrowserNavigator GetDoubleBrowserNavigator()
        {
            var wpfDoubleBrowserNavigator = new DoubleBrowserNavigator(this, _UrlSolver, _Injector);
            wpfDoubleBrowserNavigator.OnFirstLoad += FirstLoad;
            wpfDoubleBrowserNavigator.OnNavigate += OnNavigateFired;
            wpfDoubleBrowserNavigator.OnDisplay += OnDisplayFired;
            wpfDoubleBrowserNavigator.UseINavigable = _UseINavigable;
            return wpfDoubleBrowserNavigator;
        }

        private void FirstLoad(object sender, FirstLoadEvent e)
        {
            IsHTMLLoaded = true;
            OnFirstLoad?.Invoke(this, e);
        }

        private void OnNavigateFired(object sender, NavigationEvent e)
        {
            OnNavigate?.Invoke(this, e);

            if (!IsDebug)
                return;

            var modern = _WpfDoubleBrowserNavigator.HTMLWindow as IModernWebBrowserWindow;
            modern?.RegisterContextMenuItem(GetMenu())?.RegisterContextMenuItem(GetAbout());
        }

        private IEnumerable<ContextMenuItem> GetMenu()
        {
            yield return GetContextMenuItem("Inspect", _DebugInformation.DebugBrowser);
            yield return GetContextMenuItem("Vm Debug", _DebugInformation.DebugWindow, _Injector.IsSupportingVmDebug);
            yield return GetContextMenuItem("Save Vm", _DebugInformation.SaveVm);
        }

        private IEnumerable<ContextMenuItem> GetAbout()
        {
            yield return GetContextMenuItem("About Neutronium", _DebugInformation.ShowInfo);
        }

        private ContextMenuItem GetContextMenuItem(string itemName, ICommand command, bool enabled=true)
        {
            void DoAction()
            {
                if (command.CanExecute(null)) command.Execute(null);
            }

            return new ContextMenuItem(() => Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action) DoAction), itemName, enabled);
        }

        private void OnDisplayFired(object sender, DisplayEvent e)
        {
            OnDisplay?.Invoke(this, e);
        }

        private void DoShowInfo()
        {
            var windoInfo = HTMLEngineFactory.Engine.ResolveAboutScreen();
            if (windoInfo != null)
            {
                if (_LoadingAbout)
                    return;

                _LoadingAbout = true;
                var aboutWindow = new NeutroniumWindow(windoInfo.AbsolutePath, windoInfo.Framework.Name)
                {
                    Title = "About",
                    Owner = Window,
                    DataContext = new About(_WpfWebWindowFactory, _Injector),
                    Width = windoInfo.Width,
                    Height = windoInfo.Height
                };

                void Handler(object o, EventArgs e)
                {
                    aboutWindow.OnFirstLoad -= Handler;
                    aboutWindow.ShowDialog();
                    _LoadingAbout = false;
                }

                aboutWindow.OnFirstLoad += Handler;
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Neutronium.Core { VersionHelper.GetVersion(typeof(IHtmlBinding)).GetDisplayName()}")
                   .AppendLine($"WebBrowser: {_WpfWebWindowFactory.EngineName}")
                   .AppendLine($"Browser binding: {_WpfWebWindowFactory.Name}")
                   .AppendLine($"Javascript Framework: {_Injector.FrameworkName}")
                   .AppendLine($"MVVM Binding: {_Injector.Name}");
            MessageBox.Show(Window, builder.ToString(), "Neutronium configuration");
        }

        private HTMLSimpleWindow GetWHMLWindow(string path, string title, int width, int height, Func<IWebView, IDisposable> onWebViewCreated = null)
        {
            return new HTMLSimpleWindow(_WpfWebWindowFactory.Create(), path, onWebViewCreated)
            {
                Owner = Window,
                Title = title,
                Height = height,
                Width = width
            };
        }

        public IWebSessionLogger WebSessionLogger
        {
            get => _WebSessionLogger;
            set
            {
                _WebSessionLogger = value;
                if (_WpfDoubleBrowserNavigator != null)
                    _WpfDoubleBrowserNavigator.WebSessionLogger = value;
            }
        }

        public void ShowDebugWindow()
        {
            if (_VmDebugWindow != null)
            {
                var state = _VmDebugWindow.WindowState;
                _VmDebugWindow.WindowState = (state == WindowState.Minimized) ? WindowState.Normal : state;
                _VmDebugWindow.Activate();
                return;
            }
            _Injector?.DebugVm(script => WpfDoubleBrowserNavigator.ExcecuteJavascript(script),
                                (path, width, height, onCreate) => ShowHTMLWindow(path, width, height, debug => onCreate(WpfDoubleBrowserNavigator.HTMLWindow.MainFrame, debug)));

            if (_VmDebugWindow == null)
                _DebugInformation.IsDebuggingVm = !_DebugInformation.IsDebuggingVm;
            else
                _DebugInformation.IsDebuggingVm = true;
        }

        private void ShowHTMLWindow(string path, int width, int height, Func<IWebView, IDisposable> injectedCode)
        {
            _VmDebugWindow = GetWHMLWindow(path, "Neutronium ViewModel Debugger", width, height, injectedCode);
            _VmDebugWindow.Closed += _VmDebugWindow_Closed;
            _VmDebugWindow.Show();
        }

        private void _VmDebugWindow_Closed(object sender, EventArgs e)
        {
            _VmDebugWindow.Closed -= _VmDebugWindow_Closed;
            _VmDebugWindow = null;
            _DebugInformation.IsDebuggingVm = false;
        }

        public void OpenDebugBrowser()
        {
            var currentWebControl = WpfDoubleBrowserNavigator.WebControl;
            if (currentWebControl == null)
                return;

            WpfDoubleBrowserNavigator.WebControl.DebugToolOpened += WebControl_DebugToolOpened;

            var result = currentWebControl.OnDebugToolsRequest();
            if (!result)
                MessageBox.Show("Debug tools not available!");
        }

        private void WebControl_DebugToolOpened(object sender, DebugEventArgs eventArgs)
        {
            var webControl = WpfDoubleBrowserNavigator.WebControl;
            _DebugInformation.IsInspecting = eventArgs.Opening;
            if (!eventArgs.Opening && (webControl != null))
            {
                webControl.DebugToolOpened -= WebControl_DebugToolOpened;
            }
        }

        public void CloseDebugBrowser()
        {
            var currentWebControl = WpfDoubleBrowserNavigator.WebControl;
            currentWebControl?.CloseDebugTools();
        }

        protected async Task<IHtmlBinding> NavigateAsyncBase(object iViewModel, string Id = "", JavascriptBindingMode iMode = JavascriptBindingMode.TwoWay)
        {
            return await WpfDoubleBrowserNavigator.NavigateAsync(iViewModel, Id, iMode);
        }

        public void Dispose()
        {
            (_DebugControl as IDisposable)?.Dispose();
            _WpfDoubleBrowserNavigator?.Dispose();
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) e.Handled = true;
        }

        private string _WebSessionPath = null;
        public string SessionPath
        {
            get => _WebSessionPath;
            set
            {
                _WebSessionPath = value;
                if (_WebSessionPath != null)
                    Directory.CreateDirectory(_WebSessionPath);
            }
        }

        IWebBrowserWindowProvider IWebViewLifeCycleManager.Create()
        {
            var webwindow = _WpfWebWindowFactory.Create();
            var ui = webwindow.UIElement;
            Panel.SetZIndex(ui, 0);

            this.MainGrid.Children.Add(ui);
            var res = new WPFHTMLWindowProvider(webwindow, this);
            res.OnDisposed += OnWindowDisposed;
            return res;
        }

        private void OnWindowDisposed(object sender, EventArgs e)
        {
            var res = sender as WPFHTMLWindowProvider;
            res.OnDisposed -= OnWindowDisposed;
            if (_VmDebugWindow != null)
            {
                _VmDebugWindow.Close();
                _VmDebugWindow = null;
            }
        }

        IDispatcher IWebViewLifeCycleManager.GetDisplayDispatcher()
        {
            return new WPFUIDispatcher(this.Dispatcher);
        }

        public void Inject(Key keyToInject)
        {
            var wpfacess = (WpfDoubleBrowserNavigator.WebControl as WPFHTMLWindowProvider);
            if (wpfacess == null)
                return;

            var wpfweb = wpfacess.WPFWebWindow;
            wpfweb?.Inject(keyToInject);
        }

        public override string ToString() => UniqueName;
    }
}
