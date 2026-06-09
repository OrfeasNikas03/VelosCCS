using Godot;

namespace VelosCCS;

public partial class GifTextureRect : TextureRect
{
    private GifFrameData? _data;
    private int _currentFrame;
    private Timer? _timer;

    public void Play(GifFrameData data)
    {
        _data = data;
        _currentFrame = 0;
        if (data.Textures != null && data.Textures.Length > 0)
            Texture = data.Textures[0];
    }

    public override void _EnterTree()
    {
        if (_data == null) return;
        _timer = new Timer { OneShot = true };
        _timer.Timeout += OnTimerTimeout;
        AddChild(_timer);
        float delay = _data.Delays != null && _data.Delays.Length > 0 ? _data.Delays[0] : 0.1f;
        _timer.Start(delay);
    }

    public void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Timeout -= OnTimerTimeout;
            RemoveChild(_timer);
            _timer.QueueFree();
            _timer = null;
        }
    }

    private void OnTimerTimeout()
    {
        if (_data?.Textures == null || _data.Textures.Length == 0)
        {
            Stop();
            return;
        }

        _currentFrame = (_currentFrame + 1) % _data.Textures.Length;
        Texture = _data.Textures[_currentFrame];

        float delay = _data.Delays != null && _currentFrame < _data.Delays.Length
            ? _data.Delays[_currentFrame] : 0.1f;
        _timer?.Start(delay);
    }
}
