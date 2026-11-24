namespace ElectionRides.Api.Domain;

public enum RenameStatus { Pending = 1, Approved = 2, Rejected = 3 }

/// <summary>A city admin's request to rename a shared-list community; applies to every city once a system admin approves.</summary>
public class CommunityRename
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int CatalogId { get; set; }
    public Guid CityId { get; set; }
    public required string OldName { get; set; }
    public required string NewName { get; set; }
    public Guid RequestedById { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public RenameStatus Status { get; set; } = RenameStatus.Pending;
    public Guid? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// The shared list of communities (chasidut and groups) for the whole system. Each city admin picks
/// which of them appear in their city (spec 3.1); a city can still add its own communities by hand.
/// </summary>
public class CommunityCatalogItem
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The list as the party gave it (2026-11-24), in its order. Ids are fixed: new names get new ids at the end.</summary>
    public static readonly string[] Seed =
    [
        "אוז'רוב", "אוסטרובה-ביאלא", "אונגוואר", "אמשינוב", "אנטניא", "אלכסנדר", "אשלג", "באבוב", "באבוב 45", "ביאלא",
        "ביאלא בני ברק", "ביאלא בית שמש", "ביאלא פשיסחא", "ביאלא רמת אהרן", "ביטשקוב", "בישטינא", "בוהוש", "בויאן", "בוסטון", "בעלזא",
        "ברסלב", "ברסלב – צפת", "ברסלב – ירושלים", "ברסלב – יבנאל", "ברסלב – היכל הקודש", "ברסלב – חוט של חסד", "ברסלב – שובו בנים", "גור", "דז'יקוב-ויז'ניץ", "דאראג",
        "דושינסקיא", "דעעש", "הוסיאטין", "זוועהיל", "זלוטשוב", "זוטשקא", "זוטשקא אמסנא", "זוטשקא ירושלים", "חב\"ד", "טאהש",
        "טולנא", "טמשוואר", "טריסק", "לעלוב", "לעלוב ביתר", "לעלוב בית שמש", "לעלוב ירושלים", "לעלוב בני ברק", "מאקאווא", "מבקשי אמונה",
        "מודז'יץ", "מטרסדורף", "מעליץ", "מחנובקה-בעלזא", "מישקולץ", "משכנות הרועים", "נדבורנה", "נדבורנה אלעד", "נדבורנה ארד", "נדבורנה ביתר",
        "נדבורנה בני ברק", "נדבורנה ירושלים", "נדבורנה צפת", "נחלת יעקב", "סדיגורא", "סקולען", "סקווירא", "ספינקא", "ספינקא אמרי יוסף", "ספינקא צפת",
        "ספינקא רמת אהרן", "סערט ויז'ניץ", "סאסוב", "סטרופקוב", "סטריקוב", "סטוטשין", "ערלוי", "פני מנחם", "פיטסבורג", "פינסק-קרלין",
        "פרמישלאן", "צאנז", "צאנז-ז'מיגראד", "צאנז-קלויזנבורג", "טשורטקוב", "טשרנוביל", "קאליב", "מתמידים", "קוזמיר", "קופיטשניץ",
        "קומארנא", "קוסון", "קרעטשניף", "קרעטשניף כפר אתא", "קרעטשניף ירושלים", "קרעטשניף קריית גת", "קרעטשניף סיגעט", "קרעטשניף רחובות", "קרלין-סטולין", "ראדזין",
        "ראחוב", "רחמסטריווקא", "רדושיץ", "שומרי אמונים", "שאץ", "שאץ-ויז'ניץ", "שבט הלוי", "שידלובצא", "שטפנשט", "תולדות אהרן",
        "תולדות אברהם יצחק", "תורת חכם",
    ];
}
