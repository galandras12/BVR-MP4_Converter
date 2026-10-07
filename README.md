# BVR → MP4 Converter

Windows asztali alkalmazás (C# / .NET 8 / WPF), amely Blue Iris `.bvr` fájlokat konvertál `.mp4`-be.
Teljesen offline, nincs hálózati hívás és telemetria; a konvertálást az exe mellé tett `ffmpeg.exe` végzi.

## Forráskód felépítése

| Fájl | Szerep |
|---|---|
| `BvrMp4Converter/BvrMp4Converter.csproj` | projekt, single-file publish beállítások |
| `BvrMp4Converter/App.xaml(.cs)` | alkalmazás belépési pont |
| `BvrMp4Converter/MainWindow.xaml(.cs)` | felület, drag & drop, vezérlés, összesített haladás/ETA |
| `BvrMp4Converter/Models/AppSettings.cs` | beállítások mentése/olvasása a `config.ini`-ből (az exe mellett; ha ott nem írható: `%APPDATA%\BvrMp4Converter\config.ini`) |
| `BvrMp4Converter/Loc.cs` | magyar / angol felületszövegek, nyelvváltás (Nyelvek menü) |
| `BvrMp4Converter/AboutWindow.xaml(.cs)` | Névjegy ablak |
| `BvrMp4Converter/Logo.xaml`, `Assets/app.ico` | logó (kék szem, piros filmszalag) és exe-ikon |
| `BvrMp4Converter/Models/FileItem.cs` | egy listaelem (név, méret, állapot, haladás) |
| `BvrMp4Converter/Services/FfmpegTools.cs` | ffmpeg/ffprobe keresés, ffprobe, GPU-kódoló érzékelés |
| `BvrMp4Converter/Services/ConversionEngine.cs` | remux / újrakódolás, fallback, ellenőrzés, dátumátvitel, névütközés |
| `BvrMp4Converter/Services/ProcessUtil.cs` | folyamatindítás, leállítás |
| `BvrMp4Converter/Services/RunLog.cs` | naplófájl a kimeneti mappába |

## Build / publish

Szükséges: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd BvrMp4Converter
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ..\publish
```

Eredmény: `publish\BvrMp4Converter.exe` (egyetlen, self-contained exe).

## Hova kell tenni az ffmpeg.exe-t?

Az **`ffmpeg.exe`** (és lehetőleg az **`ffprobe.exe`**) kerüljön **ugyanabba a mappába, ahol a `BvrMp4Converter.exe` van**
(vagy annak `ffmpeg\` / `bin\` almappájába). Letöltés pl. a gyors build-ekből: <https://www.gyan.dev/ffmpeg/builds/>
(`ffmpeg-release-essentials`, a `bin` mappából). Ha a két fájlt a `BvrMp4Converter\` projektmappába teszed,
a build/publish automatikusan az exe mellé másolja. ffprobe nélkül a program `ffmpeg -i` kimenetéből olvassa a hosszt.
Hiányzó ffmpeg esetén a program induláskor és Start-nál érthető hibaüzenetet ad.

## Működés röviden

- **Remux (alapértelmezett):** `ffmpeg -i in.bvr -map 0:v -map 0:a? -c copy -movflags +faststart out.mp4`.
  Ha a hang kodek nem MP4-kompatibilis: `-c:v copy -c:a aac` (a kép érintetlen). A `-map 0` helyett videó+hang van mappelve,
  mert az MP4 az adat/felirat streameket gyakran elutasítja.
- **Újrakódolás:** a program kipróbálja a `*_nvenc`, `*_qsv`, `*_amf` kódolót (rövid próbakódolással), tartalék `libx264`/`libx265`.
  Minőség csúszka (CRF/CQ), felbontás-felezés.
- **Forgatás 180°:** `hflip,vflip`, mindig újrakódolással.
- Remux hiba vagy hibás/üres/rossz hosszú kimenet esetén automatikus újrapróbálás újrakódolással (a listában jelezve).
- A kimenet `.part` ideiglenes néven készül, és csak sikeres ellenőrzés után kapja meg a végleges nevét;
  a hossznak a bemenetéhez közel kell lennie (±max(1,5 mp; 2%)).
- Létrehozási és módosítási dátum átkerül a kimenetre. Az eredeti `.bvr` soha nem módosul; törlés csak bekapcsolt opcióval.
- Üres kimeneti mappa = az eredeti fájl mellé ment. A naplófájl (`bvr_convert_<dátum>.log`) a kimeneti mappába kerül;
  hibánál az ffmpeg utolsó sorai a lista alatti mezőben is láthatók.

## Tesztlista

1. **Egy fájl:** egy .bvr behúzása → remux, állapot „kész”, MP4 lejátszható, dátumok egyeznek.
2. **Több fájl:** Tallózással többes kijelölés; párhuzamosság 1, majd 3 → mind elkészül, összesített sáv és ETA frissül.
3. **Mappa:** mappa (almappákkal) ráejtése → az összes .bvr bekerül, duplikátum nem.
4. **Hiányzó ffmpeg:** ffmpeg.exe elnevezése/átmozgatása → induláskor és Start-nál hibaüzenet az elvárt mappával.
5. **Hibás .bvr:** pár bájtos / csonka fájl → „hiba” állapot, ffmpeg kimenet látható, `.part` fájl nem marad, napló létrejön.
6. **Ütköző kimeneti név:** már létező .mp4 → Kérdezzen / Felülír / Kihagy / Átnevez mind a négy beállítással; két azonos nevű forrás
   különböző mappából → második „(1)” végződést kap.
7. **Remux hiba → fallback:** nem MP4-kompatibilis videóval a megjegyzés „remux hiba → újrakódolás”.
8. **Hang (pl. PCM/G.711):** remuxnál csak a hang lesz AAC, a videó copy.
9. **Hardveres kódolás:** a kódoló címke mutatja a talált kódolót; GPU nélkül libx264.
10. **180° forgatás / felezés:** a kép helyes állású, ill. a felbontás fele.
11. **Leállítás:** futás közben Leállítás → ffmpeg leáll, nincs `.part`, a várakozók várakozók maradnak.
12. **Törlés opció:** alapból kikapcsolt; bekapcsolva megerősítést kér, és csak sikeres, ellenőrzött konvertálás után töröl.

> Megjegyzés: a kód fejlesztői környezetben (Linux) nem volt lefordítható, ezért az első Windows-os buildnél érdemes
> a fordítást és a tesztlistát végigfuttatni.
