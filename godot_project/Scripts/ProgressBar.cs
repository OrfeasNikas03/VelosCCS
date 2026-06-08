using System.Collections.Generic;

namespace VelosCCS;

public class ProgressBarStyle
{
    public int Height { get; set; } = 6;
    public string BgColor { get; set; } = "black@0.3";
    public string FillColor { get; set; } = "#FFD700";
    public bool ShowTime { get; set; } = true;
    public string Position { get; set; } = "bottom";
    public int Margin { get; set; }
}

public class ProgressBarGenerator
{
    private readonly ProgressBarStyle _style;

    public ProgressBarGenerator(ProgressBarStyle? style = null)
    {
        _style = style ?? new ProgressBarStyle();
    }

    public List<string> GetFilterChain(double duration, int width, int height)
    {
        int y = _style.Position == "bottom"
            ? height - _style.Height - _style.Margin
            : _style.Margin;

        var filters = new List<string>
        {
            $"drawbox=x=0:y={y}:w={width}:h={_style.Height}:color={_style.BgColor}:t=fill",
            $"drawbox=x=0:y={y}:w='(t/{duration})*{width}':h={_style.Height}:color={_style.FillColor}:t=fill",
        };

        if (_style.ShowTime)
        {
            filters.Add(
                $"drawtext=text='%{{pts\\:hms}}':x=w-tw-8:y={y - 20}:fontsize=14:" +
                $"fontcolor=white:shadowcolor=black@0.6:shadowx=1:shadowy=1"
            );
        }

        return filters;
    }
}
