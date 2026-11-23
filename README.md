# מטה הסעות

מערכת למטה הסעות ביום בחירות. קליטת פניות בטלפון, תור הוגן, שיבוץ נהגים מתנדבים, ותמיכה בכמה ערים.

- **אפיון:** `docs/SPEC.md`. הקובץ `docs/spec.html` נבנה ממנו בעזרת `docs/build_spec_html.py`.
- **עיצוב:** `docs/DESIGN.md` ו-`docs/green-theme.css`.
- **אבי-טיפוס לחיצים:** `docs/operator.html`, `docs/dispatcher.html`, `docs/driver.html`, `docs/admin.html`, `docs/sysadmin.html`.

## מבנה
| תיקייה | מה |
|---|---|
| `api/` | שרת ASP.NET Core (.NET 10) עם PostgreSQL ו-PostGIS (EF Core) |
| `web/` | Angular 22 עם Tailwind. מסכי המטה ואפליקציית הנהג |
| `docs/` | אפיון, עיצוב ואבי-טיפוס |
| `.github/workflows/` | בדיקה אוטומטית בכל דחיפה |

## הרצה מקומית
דרוש: Docker Desktop, .NET 10 SDK, Node 24, והכלי `dotnet-ef`.

```powershell
powershell -ExecutionPolicy Bypass -File .\dev.ps1
```

הסקריפט מרים את מסד הנתונים, מעדכן את הטבלאות, ומפעיל את השרת (פורט 5194) ואת האתר (פורט 4200).

### מנהל המערכת הראשון
בהרצה הראשונה אין משתמשים. כדי ליצור מנהל מערכת ראשון, מריצים את השרת עם האימייל שלו:

```powershell
$env:Bootstrap__AdminEmail = "name@example.com"
```

השרת יוצר אותו ושולח הזמנה. בפיתוח לא נשלחים מיילים: ההזמנה, וגם קודי הכניסה, מופיעים ביומן של חלון השרת.

## בדיקות
```powershell
dotnet test api/ElectionRides.slnx   # צריך את מסד הנתונים של docker-compose
cd web; npx ng test --watch=false
```
