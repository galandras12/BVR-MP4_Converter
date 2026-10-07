using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace BvrMp4Converter;

/// <summary>Magyar / angol felület-szövegek. A XAML-ben: {local:Tr kulcs}, kódban: Loc.T("kulcs").</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static string Lang { get; private set; } = "hu";
    public static event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => T(key);

    /// <summary>Induláskor, ha még nincs mentett nyelv: magyar rendszernyelv esetén magyar, egyébként angol.</summary>
    public static string DefaultLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hu" ? "hu" : "en";

    public static void SetLanguage(string? lang)
    {
        Lang = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "hu";
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    public static string T(string key) =>
        Strings.TryGetValue(key, out var v) ? (Lang == "en" ? v.En : v.Hu) : key;

    public static string F(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    private static readonly Dictionary<string, (string Hu, string En)> Strings = new()
    {
        // menü
        ["menu_lang"] = ("_Nyelvek", "_Language"),
        ["lang_hu"] = ("Magyar", "Hungarian"),
        ["lang_en"] = ("Angol", "English"),
        ["menu_about"] = ("_Névjegy", "_About"),

        // főablak
        ["drop_main"] = ("Húzd ide a .bvr fájlokat vagy mappákat", "Drop .bvr files or folders here"),
        ["drop_sub"] = ("(mappánál az összes .bvr rekurzívan)", "(for folders, all .bvr files recursively)"),
        ["btn_browse"] = ("Tallózás…", "Browse…"),
        ["btn_remove_sel"] = ("Kijelöltek törlése", "Remove selected"),
        ["btn_clear"] = ("Lista ürítése", "Clear list"),
        ["col_name"] = ("Név", "Name"),
        ["col_size"] = ("Méret", "Size"),
        ["col_status"] = ("Állapot", "Status"),
        ["col_progress"] = ("Haladás", "Progress"),
        ["col_note"] = ("Megjegyzés", "Note"),
        ["grp_details"] = ("ffmpeg kimenet / hiba (a kijelölt fájlhoz)", "ffmpeg output / error (for the selected file)"),
        ["grp_settings"] = ("Beállítások", "Settings"),
        ["mode_remux"] = ("Gyors 1:1 (remux)", "Fast 1:1 (remux)"),
        ["mode_reencode"] = ("Újrakódolás hardveresen", "Re-encode (hardware)"),
        ["lbl_codec"] = ("Kodek:", "Codec:"),
        ["lbl_quality"] = ("Minőség (CRF/CQ):", "Quality (CRF/CQ):"),
        ["hint_quality"] = ("(kisebb = jobb)", "(lower = better)"),
        ["chk_half"] = ("Felbontás felezése", "Halve resolution"),
        ["chk_rotate"] = ("Forgatás 180° (újrakódolást igényel)", "Rotate 180° (requires re-encoding)"),
        ["chk_delete"] = ("Sikeres konvertálás után az eredeti .bvr törlése", "Delete the original .bvr after successful conversion"),
        ["lbl_parallel"] = ("Párhuzamos feldolgozás:", "Parallel processing:"),
        ["lbl_parallel_unit"] = ("fájl egyszerre", "files at a time"),
        ["lbl_policy"] = ("Ha a kimeneti fájl már létezik:", "If the output file already exists:"),
        ["pol_ask"] = ("Kérdezzen", "Ask"),
        ["pol_overwrite"] = ("Felülír", "Overwrite"),
        ["pol_skip"] = ("Kihagy", "Skip"),
        ["pol_rename"] = ("Átnevez", "Rename"),
        ["lbl_output"] = ("Kimeneti mappa:", "Output folder:"),
        ["tip_output"] = ("Üresen hagyva a konvertált fájl az eredeti mellé kerül.", "If left empty, the converted file is saved next to the original."),
        ["btn_open_output"] = ("Mappa megnyitása", "Open folder"),
        ["btn_start"] = ("Start", "Start"),
        ["btn_stop"] = ("Leállítás", "Stop"),
        ["total_idle"] = ("—", "—"),

        // állapotok
        ["st_waiting"] = ("várakozik", "waiting"),
        ["st_running"] = ("folyamatban", "running"),
        ["st_done"] = ("kész", "done"),
        ["st_error"] = ("hiba", "error"),
        ["st_skipped"] = ("kihagyva", "skipped"),
        ["st_cancelled"] = ("megszakítva", "cancelled"),

        // kódoló / ffmpeg
        ["enc_missing"] = ("ffmpeg.exe nem található", "ffmpeg.exe not found"),
        ["enc_detecting"] = ("kódoló érzékelése…", "detecting encoder…"),
        ["enc_label"] = ("kódoló: {0}", "encoder: {0}"),
        ["msg_ffmpeg_missing"] = (
            "Az ffmpeg.exe nem található.\n\nHelyezd az ffmpeg.exe-t (és lehetőleg az ffprobe.exe-t is) a program mellé:\n{0}\n\nEnélkül a konvertálás nem indítható.",
            "ffmpeg.exe was not found.\n\nPlace ffmpeg.exe (and preferably ffprobe.exe) next to the program:\n{0}\n\nConversion cannot start without it."),
        ["title_ffmpeg_missing"] = ("Hiányzó ffmpeg", "ffmpeg missing"),

        // párbeszédek
        ["dlg_files_title"] = ("BVR fájlok kiválasztása", "Select BVR files"),
        ["dlg_files_filter"] = ("Blue Iris BVR (*.bvr)|*.bvr|Minden fájl (*.*)|*.*", "Blue Iris BVR (*.bvr)|*.bvr|All files (*.*)|*.*"),
        ["dlg_folder_title"] = ("Kimeneti mappa kiválasztása", "Select output folder"),
        ["count_text"] = ("{0} fájl, {1}", "{0} files, {1}"),
        ["msg_folder_missing"] = ("A kimeneti mappa még nem létezik.", "The output folder does not exist yet."),
        ["title_open_folder"] = ("Mappa megnyitása", "Open folder"),
        ["msg_no_files"] = ("Nincs konvertálandó fájl a listában.", "There are no files to convert in the list."),
        ["msg_outdir_fail"] = ("A kimeneti mappa nem hozható létre:\n{0}", "The output folder cannot be created:\n{0}"),
        ["title_error"] = ("Hiba", "Error"),
        ["msg_confirm_delete"] = (
            "Be van kapcsolva az eredeti .bvr fájlok törlése sikeres konvertálás után.\nBiztosan folytatod?",
            "Deleting the original .bvr files after successful conversion is enabled.\nAre you sure you want to continue?"),
        ["title_confirm"] = ("Megerősítés", "Confirmation"),
        ["msg_conflict"] = (
            "A kimeneti fájl már létezik:\n{0}\n\nIgen = felülír\nNem = átnevez (új sorszámmal)\nMégse = kihagy",
            "The output file already exists:\n{0}\n\nYes = overwrite\nNo = rename (new number)\nCancel = skip"),
        ["title_conflict"] = ("Létező kimeneti fájl", "Existing output file"),
        ["msg_exit_running"] = ("Konvertálás van folyamatban. Biztosan kilépsz?", "A conversion is in progress. Are you sure you want to exit?"),
        ["title_exit"] = ("Kilépés", "Exit"),

        // összesített haladás
        ["total_done"] = ("{0}: {1} ok, {2} hiba, {3} kihagyva", "{0}: {1} ok, {2} failed, {3} skipped"),
        ["word_done"] = ("Kész", "Finished"),
        ["word_cancelled"] = ("Megszakítva", "Cancelled"),
        ["tip_log"] = ("Napló: {0}", "Log: {0}"),
        ["eta_estimating"] = ("becslés…", "estimating…"),
        ["total_progress"] = ("{0:0}%  ({1}/{2})  hátralévő: {3}", "{0:0}%  ({1}/{2})  remaining: {3}"),

        // konvertáló motor: megjegyzések, hibák
        ["note_exists"] = ("a kimeneti fájl már létezik", "the output file already exists"),
        ["note_remux_fail"] = ("remux hiba → újrakódolás", "remux failed → re-encoding"),
        ["note_reenc"] = ("újrakódolás ({0})", "re-encoding ({0})"),
        ["note_reenc_after_remux"] = ("remux hiba → újrakódolás ({0})", "remux failed → re-encoding ({0})"),
        ["note_error"] = ("hiba: {0}", "error: {0}"),
        ["err_exit"] = ("ffmpeg kilépési kód: {0}", "ffmpeg exit code: {0}"),
        ["err_empty"] = ("a kimeneti fájl üres vagy hiányzik", "the output file is empty or missing"),
        ["err_no_video"] = ("a kimeneten nincs videó stream", "the output has no video stream"),
        ["err_no_duration"] = ("a kimenet hossza nem olvasható", "the output duration cannot be read"),
        ["err_duration_diff"] = ("a hossz eltér (be: {0:0.0}s, ki: {1:0.0}s)", "the duration differs (in: {0:0.0}s, out: {1:0.0}s)"),
        ["err_validate"] = ("ellenőrzési hiba: {0}", "verification error: {0}"),

        // névjegy
        ["about_title"] = ("Névjegy", "About"),
        ["about_ver"] = ("Verziószám: {0}", "Version: {0}"),
        ["about_dev"] = ("Fejlesztő:", "Developer:"),
        ["about_sw"] = ("Program kódért felelős softver:", "Software responsible for the program code:"),
        ["about_src"] = ("Program nyílt forráskódja:", "Open source code:"),
        ["about_lic"] = ("Licenc:", "License:"),
        ["about_req"] = ("Kötelező kiegészítő szolgáltatás:", "Required additional components:"),
        ["about_update"] = ("Új verzió ellenőrzési linkje:", "Link for checking for a new version:"),
        ["about_note"] = ("A program nem hív hálózatot; a hivatkozások csak kattintásra nyílnak meg a böngészőben.",
                          "The program makes no network calls; links open in the browser only when clicked."),
        ["btn_close"] = ("Bezárás", "Close"),
    };
}

/// <summary>XAML-ben: Text="{local:Tr kulcs}" – a nyelv váltásakor azonnal frissül.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
