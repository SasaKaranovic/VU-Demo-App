using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VU1WPF.ImageEditor
{
    /// <summary>
    /// A dial image design: base image + processing options + text items + freehand strokes.
    /// Saved as JSON (design.json) next to a copy of the base image, so it can be re-opened,
    /// edited and re-sent at any time.
    /// </summary>
    public class DialDesign
    {
        public const int DisplayWidth = 200;
        public const int DisplayHeight = 144;

        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";

        /// <summary>File name of the base image inside the design folder (null = blank white background).</summary>
        public string? BaseImageFile { get; set; }

        /// <summary>Where the base image originally came from (informational only).</summary>
        public string? BaseImageOrigin { get; set; }

        public ImageFitMode FitMode { get; set; } = ImageFitMode.Fit;
        public MonochromeMode Mode { get; set; } = MonochromeMode.Threshold;
        public int Threshold { get; set; } = 128;
        public bool Invert { get; set; }

        public List<DesignTextItem> Texts { get; set; } = new();

        /// <summary>Freehand strokes in WPF Ink Serialized Format, base64 encoded.</summary>
        public string? StrokesIsf { get; set; }

        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A text label placed on the design. Coordinates are dial pixels (0..200, 0..144).</summary>
    public class DesignTextItem : INotifyPropertyChanged
    {
        private string _text = "Text";
        private double _x;
        private double _y;
        private double _fontSize = 20;
        private string _fontFamily = "Segoe UI";
        private bool _bold;
        private bool _white;

        public string Text { get => _text; set => Set(ref _text, value ?? ""); }
        public double X { get => _x; set => Set(ref _x, value); }
        public double Y { get => _y; set => Set(ref _y, value); }
        public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 4, 144)); }
        public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value); }
        public bool Bold { get => _bold; set => Set(ref _bold, value); }

        /// <summary>White text (for dark backgrounds). Default is black.</summary>
        public bool White { get => _white; set => Set(ref _white, value); }

        public DesignTextItem Clone() => new DesignTextItem
        {
            Text = Text, X = X, Y = Y, FontSize = FontSize, FontFamily = FontFamily, Bold = Bold, White = White
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Entry shown in the "saved designs" list.</summary>
    public class DesignInfo
    {
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public string? PreviewPath { get; set; }
        public DateTime ModifiedUtc { get; set; }
        [JsonIgnore]
        public System.Windows.Media.ImageSource? Thumbnail { get; set; }
        public string ModifiedText => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>Entry shown in the template gallery.</summary>
    public class TemplateInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Source { get; set; } = "";
        public System.Windows.Media.ImageSource? Thumbnail { get; set; }
    }
}
