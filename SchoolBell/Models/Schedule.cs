namespace SchoolBell.Models;

public class Schedule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeOnly Time { get; set; }
    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
    public bool Sunday { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastTriggeredAt { get; set; }
    public int AudioFileId { get; set; }
    public AudioFile? AudioFile { get; set; }
}
