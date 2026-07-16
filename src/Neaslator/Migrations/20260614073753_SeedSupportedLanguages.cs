using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Neaslator.Migrations
{
    /// <inheritdoc />
    public partial class SeedSupportedLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "supported_languages",
                columns: new[] { "Code", "EnglishName", "IsActive", "NativeName", "SortOrder" },
                values: new object[,]
                {
                    { "ar", "Arabic", true, "العربية", (short)10 },
                    { "bg", "Bulgarian", true, "Български", (short)23 },
                    { "cs", "Czech", true, "Čeština", (short)20 },
                    { "da", "Danish", true, "Dansk", (short)16 },
                    { "de", "German", true, "Deutsch", (short)3 },
                    { "el", "Greek", true, "Ελληνικά", (short)19 },
                    { "en", "English", true, "English", (short)1 },
                    { "es", "Spanish", true, "Español", (short)5 },
                    { "fi", "Finnish", true, "Suomi", (short)18 },
                    { "fr", "French", true, "Français", (short)4 },
                    { "he", "Hebrew", true, "עברית", (short)25 },
                    { "hi", "Hindi", true, "हिन्दी", (short)30 },
                    { "hu", "Hungarian", true, "Magyar", (short)21 },
                    { "id", "Indonesian", true, "Bahasa Indonesia", (short)28 },
                    { "it", "Italian", true, "Italiano", (short)6 },
                    { "ja", "Japanese", true, "日本語", (short)12 },
                    { "ko", "Korean", true, "한국어", (short)13 },
                    { "ms", "Malay", true, "Bahasa Melayu", (short)29 },
                    { "nl", "Dutch", true, "Nederlands", (short)8 },
                    { "no", "Norwegian", true, "Norsk", (short)17 },
                    { "pl", "Polish", true, "Polski", (short)14 },
                    { "pt", "Portuguese", true, "Português", (short)7 },
                    { "ro", "Romanian", true, "Română", (short)22 },
                    { "ru", "Russian", true, "Русский", (short)9 },
                    { "sv", "Swedish", true, "Svenska", (short)15 },
                    { "th", "Thai", true, "ไทย", (short)26 },
                    { "tr", "Turkish", true, "Türkçe", (short)2 },
                    { "uk", "Ukrainian", true, "Українська", (short)24 },
                    { "vi", "Vietnamese", true, "Tiếng Việt", (short)27 },
                    { "zh", "Chinese", true, "中文", (short)11 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "ar");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "bg");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "cs");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "da");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "de");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "el");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "en");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "es");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "fi");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "fr");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "he");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "hi");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "hu");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "id");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "it");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "ja");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "ko");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "ms");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "nl");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "no");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "pl");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "pt");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "ro");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "ru");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "sv");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "th");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "tr");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "uk");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "vi");

            migrationBuilder.DeleteData(
                table: "supported_languages",
                keyColumn: "Code",
                keyValue: "zh");
        }
    }
}
