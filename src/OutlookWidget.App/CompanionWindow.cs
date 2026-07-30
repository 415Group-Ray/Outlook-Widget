using System.Runtime.InteropServices;

namespace OutlookWidget.App;

/// <summary>
/// A minimal real top-level window, existing so that brokered sign-in has a parent to attach to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a window at all, and why this one.</b> Current Microsoft documentation states that to use
/// the broker "it is now required to provide the window handle to which the WAM modal dialog be
/// parented", and gives the reason: MSAL cannot infer the parent, and getting it wrong historically
/// produced authentication dialogs hidden behind the application window. The previous probe showed a
/// message box with a null owner, which is not a window this process owns and cannot serve.
/// </para>
/// <para>
/// <b>Raw Win32 rather than WinUI, deliberately.</b> The companion project's own comment reserves the
/// WinUI 3 conversion for Phase 2, on the grounds that adding an XAML toolchain and a framework
/// dependency to a gate makes a failure ambiguous between the thing under test and the app failing to
/// start. That reasoning applies with more force here, not less: gate 8 asks whether WAM will issue a
/// token to this registration on this tenant, and the cheapest window that satisfies WAM's
/// requirement is the one that leaves the least room for a second explanation. Phase 2 replaces this
/// with the real WinUI window and the sign-in service moves onto that handle unchanged.
/// </para>
/// <para>
/// <b>The button is not decoration.</b> Microsoft's integration guidance is explicit that
/// authentication should be "invoke[d] based on user action" and that the user should be given context
/// before it happens, because WAM dialogs appearing with no attached gesture train people to type
/// credentials into unexplained prompts. So this window explains itself first and acquires a token only
/// when the button is pressed.
/// </para>
/// <para>
/// <b>Sign-in runs off the message loop.</b> The work is started on a thread-pool thread and posts a
/// private message back when it finishes. Blocking the message loop for the duration would leave this
/// window unable to repaint while WAM's dialog — a window in another process, parented to this one —
/// sat in front of it, which is exactly the "authentication window over a hung app" experience the
/// parent-handle requirement exists to prevent.
/// </para>
/// </remarks>
internal static partial class CompanionWindow
{
    private const string WindowClassName = "OutlookWidgetCompanionPhase0";
    private const string WindowTitle = "Outlook Inbox Widget — companion";

    /// <summary>The sign-in button's control identifier.</summary>
    private const int SignInButtonId = 1;

    /// <summary>
    /// Posted to the window when the sign-in task completes. <c>WM_APP</c> is the range Windows
    /// reserves for an application's own messages, so it cannot collide with a control notification.
    /// </summary>
    private const uint WmSignInFinished = WM_APP + 1;

    private static IntPtr _window;
    private static IntPtr _report;
    private static IntPtr _button;

    /// <summary>The sign-in operation, supplied by the composition root.</summary>
    private static Func<Task<string>>? _signIn;

    /// <summary>
    /// The completed sign-in report, handed from the worker to the message loop.
    /// </summary>
    /// <remarks>
    /// Written by the thread-pool continuation and read by the window procedure. The
    /// <c>PostMessage</c> that follows the write is the ordering barrier: the message cannot be
    /// dispatched before it was posted, and it is posted after the write.
    /// </remarks>
    private static string? _finishedReport;

    /// <summary>Guards against a second sign-in starting while one is in flight.</summary>
    private static int _signInRunning;

    /// <summary>
    /// The window handle, for MSAL's parent-window delegate.
    /// </summary>
    /// <remarks>
    /// Read through a delegate rather than captured as a value, because the broker client is built
    /// before <see cref="Run"/> creates the window. MSAL invokes the delegate at acquisition time, by
    /// which point this is set.
    /// </remarks>
    public static IntPtr Handle => _window;

    /// <summary>
    /// Creates the window, shows the report, and pumps messages until it closes.
    /// </summary>
    /// <param name="report">The diagnostic text to display.</param>
    /// <param name="signIn">
    /// Performs sign-in and returns the text to display afterwards. Invoked on a thread-pool thread,
    /// never on the message loop.
    /// </param>
    /// <returns>The process exit code.</returns>
    public static unsafe int Run(string report, Func<Task<string>> signIn)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(signIn);

        _signIn = signIn;

        IntPtr instance = GetModuleHandleW(null);

        fixed (char* className = WindowClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)
                    &WindowProcedure,
                hInstance = instance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),

                // The +1 is the documented encoding for a system colour rather than a brush handle.
                hbrBackground = (IntPtr)(COLOR_BTNFACE + 1),
                lpszClassName = (IntPtr)className,
            };

            if (RegisterClassExW(in windowClass) == 0)
            {
                return Marshal.GetLastPInvokeError();
            }
        }

        fixed (char* className = WindowClassName)
        fixed (char* title = WindowTitle)
        {
            _window = CreateWindowExW(
                0,
                (IntPtr)className,
                (IntPtr)title,
                WS_FIXEDWINDOW,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                WindowWidth,
                WindowHeight,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
        }

        if (_window == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError();
        }

        CreateChildren(instance, report);

        ShowWindow(_window, SW_SHOWNORMAL);
        UpdateWindow(_window);

        MSG message;

        while (GetMessageW(out message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }

        return 0;
    }

    private const int WindowWidth = 760;
    private const int WindowHeight = 560;
    private const int Margin = 12;
    private const int ButtonWidth = 150;
    private const int ButtonHeight = 34;

    /// <summary>
    /// Creates the report box and the sign-in button, sized from the window's actual client area.
    /// </summary>
    /// <remarks>
    /// <b>Laid out from <c>GetClientRect</c> rather than from <see cref="WindowHeight"/>.</b> The first
    /// version derived child positions by subtracting guessed multiples of the margin from the
    /// *outer* window size, which is the wrong quantity: <c>CreateWindowEx</c> takes the outer
    /// dimensions including the caption and borders, and the client area is smaller by an amount that
    /// depends on the frame metrics rather than on anything in this file. The arithmetic happened to
    /// put the button's lower edge past the bottom of the client area, so it rendered visibly cut off —
    /// and the report box stopped well short of the right margin for the same reason in the other
    /// axis. Asking the window how big it actually is removes the guess instead of correcting it.
    /// </remarks>
    private static unsafe void CreateChildren(IntPtr instance, string report)
    {
        GetClientRect(_window, out RECT client);

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;

        // A read-only multi-line EDIT rather than a STATIC, for two reasons that matter for a
        // diagnostic surface: it renders newlines dependably, and its content can be selected and
        // copied. A support report the user cannot copy out of the window is much less useful.
        fixed (char* editClass = "EDIT")
        fixed (char* text = report)
        {
            _report = CreateWindowExW(
                0,
                (IntPtr)editClass,
                (IntPtr)text,
                WS_CHILD | WS_VISIBLE | WS_BORDER | WS_VSCROLL
                    | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
                Margin,
                Margin,
                width - (Margin * 2),
                height - ButtonHeight - (Margin * 3),
                _window,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
        }

        fixed (char* buttonClass = "BUTTON")
        fixed (char* caption = "Sign in")
        {
            _button = CreateWindowExW(
                0,
                (IntPtr)buttonClass,
                (IntPtr)caption,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                Margin,
                height - ButtonHeight - Margin,
                ButtonWidth,
                ButtonHeight,
                _window,
                (IntPtr)SignInButtonId,
                instance,
                IntPtr.Zero);
        }

        // Without this the controls render in the 1980s bitmap system font. The stock GUI font is not
        // the modern UI font either, but it is legible, and this window is replaced in Phase 2.
        IntPtr font = GetStockObject(DEFAULT_GUI_FONT);
        SendMessageW(_report, WM_SETFONT, font, 1);
        SendMessageW(_button, WM_SETFONT, font, 1);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_COMMAND when (wParam.ToInt64() & 0xFFFF) == SignInButtonId:
                BeginSignIn();
                return IntPtr.Zero;

            case WmSignInFinished:
                CompleteSignIn();
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Starts sign-in on a thread-pool thread, at most one at a time.
    /// </summary>
    private static void BeginSignIn()
    {
        if (Interlocked.Exchange(ref _signInRunning, 1) == 1)
        {
            return;
        }

        SetButtonText("Signing in…");

        Func<Task<string>> signIn = _signIn!;

        // Fire and forget on purpose: the result comes back through the posted message, not through
        // this task. The continuation catches everything, because an unobserved exception here would
        // leave the button stuck reading "Signing in…" with no explanation on screen.
        _ = Task.Run(async () =>
        {
            string report;

            try
            {
                report = await signIn().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The type name only. An exception message from an authentication stack routinely
                // carries an account or a raw server response, and section 6 forbids surfacing it.
                report = "Sign-in failed unexpectedly: " + e.GetType().Name;
            }

            _finishedReport = report;
            PostMessageW(_window, WmSignInFinished, IntPtr.Zero, IntPtr.Zero);
        });
    }

    private static unsafe void CompleteSignIn()
    {
        string report = _finishedReport ?? "Sign-in produced no report.";

        fixed (char* text = report)
        {
            SetWindowTextW(_report, (IntPtr)text);
        }

        SetButtonText("Sign in again");
        Volatile.Write(ref _signInRunning, 0);
    }

    private static unsafe void SetButtonText(string caption)
    {
        fixed (char* text = caption)
        {
            SetWindowTextW(_button, (IntPtr)text);
        }
    }

    // Win32 constants. Named rather than inlined so the style expressions above stay readable.
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint WS_TABSTOP = 0x00010000;

    /// <summary>
    /// <c>WS_OVERLAPPEDWINDOW</c> without <c>WS_THICKFRAME</c> or <c>WS_MAXIMIZEBOX</c>: the child
    /// controls are placed at fixed coordinates and this window handles no <c>WM_SIZE</c>, so a
    /// resizable frame would only offer the user a way to make it look broken.
    /// </summary>
    private const uint WS_FIXEDWINDOW = 0x00CA0000;

    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint ES_READONLY = 0x0800;
    private const uint BS_DEFPUSHBUTTON = 0x0001;

    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOWNORMAL = 1;
    private const int COLOR_BTNFACE = 15;
    private const int DEFAULT_GUI_FONT = 17;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_APP = 0x8000;

    private static readonly IntPtr IDC_ARROW = 32512;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle,
        IntPtr lpClassName,
        IntPtr lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowTextW(IntPtr hWnd, IntPtr lpString);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr GetStockObject(int i);
}
