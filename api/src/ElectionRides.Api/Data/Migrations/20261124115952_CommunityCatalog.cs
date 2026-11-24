using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommunityCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CatalogId",
                table: "Communities",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommunityCatalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityCatalog", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "CommunityCatalog",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "אוז'רוב" },
                    { 2, "אוסטרובה-ביאלא" },
                    { 3, "אונגוואר" },
                    { 4, "אמשינוב" },
                    { 5, "אנטניא" },
                    { 6, "אלכסנדר" },
                    { 7, "אשלג" },
                    { 8, "באבוב" },
                    { 9, "באבוב 45" },
                    { 10, "ביאלא" },
                    { 11, "ביאלא בני ברק" },
                    { 12, "ביאלא בית שמש" },
                    { 13, "ביאלא פשיסחא" },
                    { 14, "ביאלא רמת אהרן" },
                    { 15, "ביטשקוב" },
                    { 16, "בישטינא" },
                    { 17, "בוהוש" },
                    { 18, "בויאן" },
                    { 19, "בוסטון" },
                    { 20, "בעלזא" },
                    { 21, "ברסלב" },
                    { 22, "ברסלב – צפת" },
                    { 23, "ברסלב – ירושלים" },
                    { 24, "ברסלב – יבנאל" },
                    { 25, "ברסלב – היכל הקודש" },
                    { 26, "ברסלב – חוט של חסד" },
                    { 27, "ברסלב – שובו בנים" },
                    { 28, "גור" },
                    { 29, "דז'יקוב-ויז'ניץ" },
                    { 30, "דאראג" },
                    { 31, "דושינסקיא" },
                    { 32, "דעעש" },
                    { 33, "הוסיאטין" },
                    { 34, "זוועהיל" },
                    { 35, "זלוטשוב" },
                    { 36, "זוטשקא" },
                    { 37, "זוטשקא אמסנא" },
                    { 38, "זוטשקא ירושלים" },
                    { 39, "חב\"ד" },
                    { 40, "טאהש" },
                    { 41, "טולנא" },
                    { 42, "טמשוואר" },
                    { 43, "טריסק" },
                    { 44, "לעלוב" },
                    { 45, "לעלוב ביתר" },
                    { 46, "לעלוב בית שמש" },
                    { 47, "לעלוב ירושלים" },
                    { 48, "לעלוב בני ברק" },
                    { 49, "מאקאווא" },
                    { 50, "מבקשי אמונה" },
                    { 51, "מודז'יץ" },
                    { 52, "מטרסדורף" },
                    { 53, "מעליץ" },
                    { 54, "מחנובקה-בעלזא" },
                    { 55, "מישקולץ" },
                    { 56, "משכנות הרועים" },
                    { 57, "נדבורנה" },
                    { 58, "נדבורנה אלעד" },
                    { 59, "נדבורנה ארד" },
                    { 60, "נדבורנה ביתר" },
                    { 61, "נדבורנה בני ברק" },
                    { 62, "נדבורנה ירושלים" },
                    { 63, "נדבורנה צפת" },
                    { 64, "נחלת יעקב" },
                    { 65, "סדיגורא" },
                    { 66, "סקולען" },
                    { 67, "סקווירא" },
                    { 68, "ספינקא" },
                    { 69, "ספינקא אמרי יוסף" },
                    { 70, "ספינקא צפת" },
                    { 71, "ספינקא רמת אהרן" },
                    { 72, "סערט ויז'ניץ" },
                    { 73, "סאסוב" },
                    { 74, "סטרופקוב" },
                    { 75, "סטריקוב" },
                    { 76, "סטוטשין" },
                    { 77, "ערלוי" },
                    { 78, "פני מנחם" },
                    { 79, "פיטסבורג" },
                    { 80, "פינסק-קרלין" },
                    { 81, "פרמישלאן" },
                    { 82, "צאנז" },
                    { 83, "צאנז-ז'מיגראד" },
                    { 84, "צאנז-קלויזנבורג" },
                    { 85, "טשורטקוב" },
                    { 86, "טשרנוביל" },
                    { 87, "קאליב" },
                    { 88, "מתמידים" },
                    { 89, "קוזמיר" },
                    { 90, "קופיטשניץ" },
                    { 91, "קומארנא" },
                    { 92, "קוסון" },
                    { 93, "קרעטשניף" },
                    { 94, "קרעטשניף כפר אתא" },
                    { 95, "קרעטשניף ירושלים" },
                    { 96, "קרעטשניף קריית גת" },
                    { 97, "קרעטשניף סיגעט" },
                    { 98, "קרעטשניף רחובות" },
                    { 99, "קרלין-סטולין" },
                    { 100, "ראדזין" },
                    { 101, "ראחוב" },
                    { 102, "רחמסטריווקא" },
                    { 103, "רדושיץ" },
                    { 104, "שומרי אמונים" },
                    { 105, "שאץ" },
                    { 106, "שאץ-ויז'ניץ" },
                    { 107, "שבט הלוי" },
                    { 108, "שידלובצא" },
                    { 109, "שטפנשט" },
                    { 110, "תולדות אהרן" },
                    { 111, "תולדות אברהם יצחק" },
                    { 112, "תורת חכם" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Communities_CatalogId",
                table: "Communities",
                column: "CatalogId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityCatalog_Name",
                table: "CommunityCatalog",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Communities_CommunityCatalog_CatalogId",
                table: "Communities",
                column: "CatalogId",
                principalTable: "CommunityCatalog",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Communities_CommunityCatalog_CatalogId",
                table: "Communities");

            migrationBuilder.DropTable(
                name: "CommunityCatalog");

            migrationBuilder.DropIndex(
                name: "IX_Communities_CatalogId",
                table: "Communities");

            migrationBuilder.DropColumn(
                name: "CatalogId",
                table: "Communities");
        }
    }
}
