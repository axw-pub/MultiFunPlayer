﻿using Linearstar.Windows.RawInput;
using Linearstar.Windows.RawInput.Native;
using MultiFunPlayer.Common;
using NLog;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Interop;

namespace MultiFunPlayer.Input.RawInput;

internal sealed class RawInputProcessor : IInputProcessor
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly HashSet<Key> _pressedKeys;
    private HwndSource _source;

    private long _lastMouseAxisTimestamp;
    private double _mouseXAxis, _mouseYAxis;
    private double _mouseDeltaXAccum, _mouseDeltaYAccum;
    private double _mouseWheelAxis, _mouseHorizontalWheelAxis;

    public event EventHandler<IInputGesture> OnGesture;

    public RawInputProcessor()
    {
        _pressedKeys = [];
        _lastMouseAxisTimestamp = 0;
        _mouseXAxis = _mouseYAxis = 0.5;
        _mouseWheelAxis = _mouseHorizontalWheelAxis = 0.5;
    }

    public void RegisterWindow(HwndSource source)
    {
        if (_source != null)
            throw new InvalidOperationException("Cannot register more than one window");

        _source = source;

        source.AddHook(MessageSink);

        RawInputDevice.RegisterDevice(HidUsageAndPage.Keyboard, RawInputDeviceFlags.ExInputSink, source.Handle);
        RawInputDevice.RegisterDevice(HidUsageAndPage.Mouse, RawInputDeviceFlags.ExInputSink, source.Handle);
    }

    private IntPtr MessageSink(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_INPUT = 0x00FF;

        if (msg == WM_INPUT)
        {
            var data = RawInputData.FromHandle(lParam);
            Logger.Trace(data);

            switch (data)
            {
                case RawInputKeyboardData keyboard:
                    ParseKeyboardGestures(keyboard);
                    break;
                case RawInputMouseData mouse:
                    ParseMouseGestures(mouse);
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void ParseKeyboardGestures(RawInputKeyboardData data)
    {
        var key = KeyInterop.KeyFromVirtualKey(data.Keyboard.VirutalKey);
        var pressed = (data.Keyboard.Flags & RawKeyboardFlags.Up) == 0;

        if (pressed)
        {
            _pressedKeys.Add(key);
        }
        else
        {
            if (_pressedKeys.Count > 0)
                HandleGesture(KeyboardGesture.Create(_pressedKeys));
            _pressedKeys.Clear();
        }
    }

    public void ParseMouseGestures(RawInputMouseData data)
    {
        bool HasFlag(RawMouseButtonFlags flag) => data.Mouse.Buttons.HasFlag(flag);

        if (HasFlag(RawMouseButtonFlags.Button4Down)) HandleGesture(MouseButtonGesture.Create(MouseButton.XButton1));
        else if (HasFlag(RawMouseButtonFlags.Button5Down)) HandleGesture(MouseButtonGesture.Create(MouseButton.XButton2));
        else if (HasFlag(RawMouseButtonFlags.LeftButtonDown)) HandleGesture(MouseButtonGesture.Create(MouseButton.Left));
        else if (HasFlag(RawMouseButtonFlags.RightButtonDown)) HandleGesture(MouseButtonGesture.Create(MouseButton.Right));
        else if (HasFlag(RawMouseButtonFlags.MiddleButtonDown)) HandleGesture(MouseButtonGesture.Create(MouseButton.Middle));

        if (data.Mouse.ButtonData != 0)
        {
            var delta = data.Mouse.ButtonData / (120d * 50d);
            if (HasFlag(RawMouseButtonFlags.MouseWheel))
            {
                _mouseWheelAxis = MathUtils.Clamp01(_mouseWheelAxis + delta);
                HandleGesture(MouseAxisGesture.Create(MouseAxis.MouseWheel, _mouseWheelAxis, delta, 0));
            }
            else if (HasFlag(RawMouseButtonFlags.MouseHorizontalWheel))
            {
                _mouseHorizontalWheelAxis = MathUtils.Clamp01(_mouseHorizontalWheelAxis + delta);
                HandleGesture(MouseAxisGesture.Create(MouseAxis.MouseHorizontalWheel, _mouseHorizontalWheelAxis, delta, 0));
            }
        }

        var deltaX = data.Mouse.LastX / 500d;
        var deltaY = data.Mouse.LastY / 500d;

        _mouseDeltaXAccum += deltaX;
        _mouseDeltaYAccum += deltaY;
        _mouseXAxis = MathUtils.Clamp01(_mouseXAxis + deltaX);
        _mouseYAxis = MathUtils.Clamp01(_mouseYAxis + deltaY);

        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = (timestamp - _lastMouseAxisTimestamp) / (double)Stopwatch.Frequency;
        if (elapsed >= 0.01)
        {
            _lastMouseAxisTimestamp = timestamp;

            if (Math.Abs(_mouseDeltaXAccum) > 0.000001)
                HandleGesture(MouseAxisGesture.Create(MouseAxis.X, _mouseXAxis, _mouseDeltaXAccum, 0));

            if (Math.Abs(_mouseDeltaYAccum) > 0.000001)
                HandleGesture(MouseAxisGesture.Create(MouseAxis.Y, _mouseYAxis, _mouseDeltaYAccum, 0));

            _mouseDeltaXAccum = 0;
            _mouseDeltaYAccum = 0;
        }
    }

    private void HandleGesture(IInputGesture gesture)
        => OnGesture?.Invoke(this, gesture);

    private void Dispose(bool disposing)
    {
        _source?.RemoveHook(MessageSink);

        RawInputDevice.UnregisterDevice(HidUsageAndPage.Keyboard);
        RawInputDevice.UnregisterDevice(HidUsageAndPage.Mouse);

        _source = null;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
