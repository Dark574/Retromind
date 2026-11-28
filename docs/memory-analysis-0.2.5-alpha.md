# Speicheranalyse für Retromind 0.2.5-alpha

**Etappe 3b – Untersuchung des Ressourcenlebenszyklus abgeschlossen**

Der historische Bericht einschließlich Etappe 1 wurde aus der erhaltenen Messkopie
rekonstruiert. Die damaligen Ausgangswerte und Einschränkungen bleiben historische
Befunde. Der Ausgangsstand der neuen Vergleiche ist `d8fa12f`.

**Aktuelle Entscheidung:** Ursprüngliche, begrenzte A/B-Wiederverwendung für Bilder
und Video beibehalten. Sofortige Freigabe, Freigabe bei Kanalende, die zusätzliche
Playback-Lifetime und Bildfreigabe nach jeder Blende werden nicht übernommen.
Der kleine Sicherheitsstand **T** erhält Animations-/Surface-Guards, die Sperre für
nie angehängte Slots und den Schutz ungefüllter Bitmaps. **2.580 vollständige und
27 gezielte Regressionstests bestehen**, der Release-Build ist fehlerfrei. VLC-
Surface/ViewModel und Etappe 1 bleiben unverändert.

**Keine uneingeschränkte PSS-Freigabe:** Im regulären 4K-Viererblock liegt T günstiger
als A; im zusätzlichen Fünf-Minuten-Einzelpaar liegt T nach Abbau dagegen bei
**1.675,3 gegenüber 1.440,5 MiB**. Beide bleiben bei null beobachteten nativen
Besitzern/Puffern nahezu konstant. Ein allgemeiner PSS-Vorteil ist damit nicht
bewiesen; die absoluten 4K-Ziele werden weiterhin verfehlt. Historische
Etappe-3-Einsparungen unten gehören zu verworfenen
Kandidaten und sind kein Nutzenversprechen für T.

[Etappe 3b und Messergebnisse](#etappe-3b--ressourcenfreigabe-und-bitmap-wiederverwendung).

Die folgenden neuen Abschnitte ergänzen diese Befunde; experimentelle Decoder- und
Player-Varianten aus Etappe 2 werden nicht in den Produktivcode übernommen.
Die historischen Rohdaten von Etappe 3 liegen unter `/tmp/retromind-stage3`.
Die aktuellen Etappe-3b-Skripte und kompakten Ergebnisse werden dagegen dauerhaft
unter `benchmarks/video-slot-lifetime/` gesichert; Medien und große Rohdaten bleiben
außerhalb des vorgeschlagenen Repository-Inhalts.

[Umsetzung und Messung von Etappe 1](#umsetzung-und-messung-von-etappe-1)

Die produktive Messreihe zeigt einen Höchstwert von 2.372,0 MiB PSS im BigMode und 2.112,8 MiB fünf Minuten nach der Rückkehr zum Desktop. Der isolierte Kontrolllauf zeigt, dass Videovorschauen die Prozessspeicherbelegung deutlich erhöhen können. Nach dem Schließen meldete glibc in einem kürzeren Diagnoselauf 425,6 MiB freie Arena-Blöcke. Im VLC-Pfad lässt sich zusätzlich eine fehlende Freigabe nativer Medienreferenzen direkt im Quellcode bestätigen. Wie viel PSS dieser Fehler und die freien Arena-Blöcke in der produktiven Messreihe erklären, bleibt offen.

## Produktive Ausgangsmessung

Retromind 0.2.5-alpha unter CachyOS, etwa 5.000 Titel, überwiegend automatischer Medienwechsel im AttractMode. Die acht Werte stammen aus der bereitgestellten Messreihe. Der zusätzliche Speicher entfiel laut Messnotiz weitgehend auf `Private_Dirty`. Swap blieb unbenutzt. Für diese Reihe liegen keine zeitgleichen Heap-, Allocator- oder GPU-Daten vor.

| Zeitpunkt | PSS in MiB | Differenz zum initialen Desktop |
|---|---:|---:|
| Desktop initial | 530,6 | 0,0 |
| BigMode nach 5 Minuten | 1.903,2 | +1.372,6 |
| BigMode nach 12 Minuten | 2.372,0 | +1.841,4 |
| BigMode nach 30 Minuten | 2.100,6 | +1.570,0 |
| Rückkehr zum Desktop | 2.113,1 | +1.582,5 |
| Desktop nach 5 Minuten Leerlauf | 2.112,8 | +1.582,2 |
| Erneuter BigMode-Start | 2.114,9 | +1.584,3 |
| Zweiter BigMode nach 15 Minuten | 2.185,9 | +1.655,3 |

Der Rückgang um 271,4 MiB zwischen Minute 12 und 30 und der geringe Anstieg beim Wiederöffnen passen zu einem Höchststand mit teilweiser Wiederverwendung. Ein kleineres, gleichzeitig aktives Leck bleibt möglich. `Private_Dirty` beschreibt private, geänderte Seiten. Darin können unter anderem GC-Segmente, native Heaps, private Copy-on-Write-Seiten und bestimmte Grafikpuffer liegen. [Linux-Kernel-Dokumentation zu procfs](https://kernel.org/doc/html/v7.2/filesystems/proc.html)

## Kontrollmessung und Speicheraufteilung

Das verfügbare Distributionsmanifest nennt Commit `aa5f3cd` mit lokal geändertem Arbeitsbaum, .NET 10.0.15 und VLC 3.0.24. Die SHA-256 des gemessenen AppImage lautet `6be4997eb95aaa8f9cc71264b985cc609be9a30d4d5ad8ba6597ea03a62f7d04`. Die Fundstellen in diesem Bericht beziehen sich auf den Checkout `12667a3` mit Avalonia 12.1.6 und LibVLCSharp 3.10.2. Für den exakten Stand der produktiven Messung fehlt eine eindeutige Build-ID.

Kontrollumgebung war eine getrennte virtuelle KWin/Xwayland-Sitzung auf CachyOS mit Host-glibc 2.44, 1.920 × 1.080 bei 60 Hz und AMD Radeon 8060S mit gemeinsam genutztem Speicher. Ein entpacktes AppImage lief über sein `AppRun` mit eigenem portablem Datenverzeichnis je Variante. Alle drei Bibliotheken enthielten 5.000 synthetische Titel. Die aktive Kategorie enthielt 256 Titel mit 64 wiederverwendeten 3.200 × 1.800-PNGs und JPEG-Covern. Die Videovarianten hatten vier stumme H.264-Testclips mit 1.920 × 1.080 bei 24 FPS und Software-Decoding. Die Dateien lagen im verwalteten Bibliotheksschema. Ein temporärer .NET-Startup-Hook rief die regulären Commands zum Öffnen, Weiterblättern und Schließen auf und erfasste Auswahl-, Attract- und Wiedergabezustände. Er lief in allen Varianten. Die produktive Bibliothek wurde für diese Kontrollmessung nicht verwendet.

### PSS-Vergleich

Jede Variante lief einmal in einem frischen Retromind-Prozess. Auf den Desktop und den BigMode-Start folgten vier feste Auswahlschritte mit je etwa vier Sekunden Haltezeit, anschließend 95 Sekunden ohne Eingabe. Der automatische Attract-Spin wurde in den Zustandsprotokollen aller Varianten beobachtet. Die erste Rückkehr zum Desktop wurde rund 20 Sekunden beobachtet, danach folgten ein zweiter BigMode-Aufruf und eine zweite Rückkehr. Die Tabelle gibt den Median der letzten bis zu fünf 2-Sekunden-Abtastungen je Phase an. Spitzen sind jeweils der höchste erfasste Stichprobenwert.

| Phase in MiB PSS | Navigator Bilder | Navigator Video | Prism Video |
|---|---:|---:|---:|
| Desktop vor BigMode | 266,7 | 262,8 | 254,4 |
| Erster BigMode-Start | 344,2 | 496,8 | 601,1 |
| Vier feste Auswahlschritte | 472,6 | 853,7 | 861,2 |
| Ende der Phase mit Attract | 598,4 | 1.129,5 | 1.283,5 |
| Höchste Stichprobe in der ersten BigMode-Sitzung | 599,6 | 1.224,6 | 1.283,6 |
| Desktop nach erster Rückkehr | 608,5 | 1.086,2 | 1.108,4 |
| Erneuter BigMode-Start | 575,3 | 1.223,3 | 1.156,3 |
| Desktop nach zweiter Rückkehr | 579,3 | 1.130,7 | 1.043,7 |

Navigator mit Video lag nach vier festen Auswahlschritten rund **381 MiB**, am Ende der Attract-Phase rund **531 MiB** und nach der Rückkehr rund **478 MiB** über Navigator ohne Videodateien. Der Vergleich umfasst den gesamten Videopfad einschließlich des davon abhängigen Artwork-Wechsels. Attract wählt am Ende zufällig. Die späten Phasen haben deshalb keine garantierte identische Titelauswahl. Prism lag beim BigMode-Start rund 104 MiB über Navigator mit Video. Für eine allgemeine Rangfolge der Themes sind Wiederholungen mit identischen Medienfolgen nötig.

### Aufteilung im Navigator-Kontrolllauf mit Video

| Median der Phase in MiB | PSS | PSS anonym | PSS Dateien | PSS Shmem | Private Dirty | RSS |
|---|---:|---:|---:|---:|---:|---:|
| Desktop vor BigMode | 262,8 | 156,4 | 12,2 | 94,3 | 247,6 | 361,7 |
| Ende der Phase mit Attract | 1.129,5 | 976,7 | 12,2 | 140,6 | 1.113,1 | 1.231,3 |
| Desktop nach erster Rückkehr | 1.086,2 | 931,8 | 12,2 | 142,2 | 1.069,8 | 1.187,8 |

`SwapPss` blieb in allen drei Läufen bei 0. Im Navigator-Videolauf stieg die anonyme PSS nach der Rückkehr um rund 775 MiB gegenüber der Desktop-Baseline. Die volle Mapping-Stichprobe wies etwa 900 MiB PSS in unbenannten privaten Mappings aus, gegenüber 126 MiB beim Anfangssnapshot. Das Haupt-`[heap]`-Mapping blieb bei rund 22 MiB. Weitere glibc-Arenen und GC-Segmente können in unbenannten Mappings liegen.

| Letzte beobachtete GC im Navigator-Videolauf, MiB | Heap inkl. Fragmentierung | Fragmentierung | Heap abzüglich Fragmentierung | Committed | LOH | POH |
|---|---:|---:|---:|---:|---:|---:|
| Desktop vor BigMode | 36,0 | 0,3 | 35,7 | 59,4 | 0,3 | 0,1 |
| Ende der Phase mit Attract | 83,4 | 11,8 | 71,6 | 100,8 | 28,6 | 0,1 |
| Desktop nach erster Rückkehr | 113,2 | 43,5 | 69,7 | 141,9 | 28,6 | 0,1 |

Beim Desktop nach Rückkehr entfielen 27,6 MiB Fragmentierung auf den LOH und 15,8 MiB auf Gen 2. Über die beobachtete Zeit wurden drei Gen-2-Sammlungen gezählt. Der Heap abzüglich Fragmentierung blieb am ersten Desktop-Rückkehrpunkt etwas unter dem BigMode-Wert. Der Anstieg des Managed Heap ist erheblich kleiner als der PSS-Anstieg. Im bilderbasierten Kontrolllauf lagen nach der ersten Rückkehr 89,3 MiB Heap inklusive 39,8 MiB Fragmentierung vor. LOH-Größen und Ursachen sind ohne Heap-Typdaten getrennt zu prüfen.

Die AMD-Treiberwerte des Navigator-Videolaufs lagen als VRAM/GTT bei 81,5/28,0 MiB initial, 84,2/22,0 MiB am Ende der Attract-Phase und 98,2/36,0 MiB nach der ersten Rückkehr. Der VRAM-Wert enthielt in diesen drei Punkten jeweils rund 8,8 MiB geteilte Seiten. Im Prism-Videolauf wurden am Ende der Attract-Phase 136,7 MiB VRAM und 57,5 MiB GTT ausgewiesen. Für die Hunderte MiB zusätzlicher Navigator-PSS zeigte sich kein Anstieg vergleichbarer Größenordnung in diesen DRM-Zählern. Retromind setzt kein eigenes Skia-Limit. Avalonia 12.1.6 verwendet für seinen Ressourcen-Cache standardmäßig ungefähr 28 MiB. Ausgaben des Compositors, Treiberallokationen und andere Render-Targets folgen eigenen Lebenszyklen. [Avalonia SkiaOptions](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Skia/Avalonia.Skia/SkiaOptions.cs#L7)

### Separater glibc-Lauf zu freien und wiederverwendbaren Blöcken

Navigator mit denselben Videos, vier festen Wechseln und anschließend nur zwölf Sekunden Leerlauf vor dem Schließen. In dieser Zeit startete kein Attract-Spin. Der zusätzliche Startup-Hook las `malloc_info(0)`, die Bildcache-Nutzung und schwache Referenzen auf die geöffneten Hosts und ViewModels aus. Die Messwerte stammen aus den jeweiligen Zustandssnapshots.

| Zustand | Prozess-PSS, MiB | glibc-Arenen aktuell, MiB | Von glibc als frei gemeldet, MiB | Direkte glibc-`mmap`-Blöcke, MiB | Bild-LRU Pixelbytes, MiB |
|---|---:|---:|---:|---:|---:|
| Desktop initial | 236,7 | 59,6 | 19,7 | 3,7 | 10,4 |
| Erster BigMode-Start | 479,6 | 294,5 | 53,4 | 19,0 | 11,6 |
| BigMode nach vier Wechseln | 661,0 | 612,3 | 355,2 | 3,7 | 11,6 |
| Desktop nach erster Rückkehr | 583,1 | 508,2 | 425,6 | 3,7 | 18,0 |
| Erneuter BigMode-Start | 833,7 | 766,5 | 486,9 | 3,7 | 18,0 |
| Desktop nach zweiter Rückkehr | 566,6 | 472,5 | 381,2 | 3,7 | 18,0 |

Nach dem ersten Schließen blieben **425,6 MiB freie Blöcke in den glibc-Arenen**, rund 406 MiB mehr als am Desktop zu Beginn. Gleichzeitig sank die Zahl der Threads von 48 im BigMode auf 38 nach Rückkehr. Das bestätigt nennenswerten bereits freigegebenen Speicher im nativen Heap. Die freie Blockgröße misst keine seitengenaue Residenz und enthält auch Platz, der durch die Verteilung anderer Blöcke nicht zurückgegeben werden kann. Deshalb lässt sich daraus weder eine exakte PSS-Zuordnung noch ein Einsparwert von 425,6 MiB ableiten. Der Bild-LRU blieb bei höchstens 18,0 MiB in diesem Lauf. [Linux-Referenz zu malloc_info](https://man7.org/linux/man-pages/man3/malloc_info.3.html)

Beim ersten Desktop-Rückkehrpunkt waren der alte Host und sein ViewModel per WeakReference noch lebend. Nach der zweiten Rückkehr lebten je zwei Instanzen. Die gemeldete Zahl der Gen-2-Sammlungen stieg zwischen dem ersten BigMode-Snapshot und dem erneuten Öffnen von drei auf fünf. Nach dem zweiten Schließen wurde in der kurzen Beobachtung keine weitere Gen-2-Sammlung erfasst. Der nächste Objektgraph sollte den alten Host und das ViewModel auf Roots untersuchen. Die kurze Stichprobe erlaubt keine Aussage über dauerhaft gehaltene Sessions.

### Bedeutung der Messgrößen

- **Prozessspeicher.** `Pss_Anon + Pss_File + Pss_Shmem` ergibt innerhalb der Rundung PSS. `Private_Dirty`, RSS, `Anonymous` und GPU-Treiberwerte sind überlappende Sichtweisen. `smaps` ordnet Seiten Mappings zu. Ein unbenanntes privates Mapping lässt sich daraus allein weder einem Allokator noch einem lebenden Objekt zuordnen. [Linux-Kernel-Dokumentation zu procfs](https://kernel.org/doc/html/v7.2/filesystems/proc.html)
- **Managed Heap.** `dotnet-counters` 10.0.750501 konnte an den selbstenthaltenden AppImage-Prozess anhängen. Die Heap-Größen und Fragmentierung für Gen 0–2, LOH und POH stammen aus der letzten beobachteten GC. Die Heap-Größe enthält Fragmentierung. Die Differenz zur Fragmentierung schätzt belegten Objektspeicher am GC-Zeitpunkt. Erreichbarkeit zu einem späteren Zeitpunkt erfordert einen Objektgraphen. GC-Committed misst zugesicherte virtuelle Bytes und darf von PSS nicht abgezogen werden. Der kumulative Allokationszähler dient zur Ermittlung der Rate. Gepinnte Objekte können auch außerhalb des POH liegen. [Microsoft-Dokumentation zu den Laufzeitmetriken](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)
- **Nativer Speicher.** Prozessseiten mit Bitmap-, Codec-, Treiber- und Allocator-Daten können in denselben PSS-Kategorien erscheinen. Eine native Allokationsspur mit Allokations- und Freigabestacks beantwortet, wie viele Bytes weiter aktiv sind. `malloc_info` kann auf glibc zusätzlich Arena-Größen und als frei gemeldete Blöcke sichtbar machen. Beide Werte sind unabhängig von der Residenz einzelner Seiten. `heaptrack` war lokal nicht installiert. [Heaptrack](https://github.com/KDE/heaptrack)
- **GPU.** `/proc/<PID>/fdinfo` wies AMD-DRM-Daten aus. Mehrere FDs mit demselben Gerät und `drm-client-id` wurden einmal ausgewertet. `drm-resident-vram` und `drm-resident-gtt` enthalten bereits die jeweils geteilten Teilmengen. Sie werden separat vom Prozess-PSS betrachtet. Auf der UMA-GPU können dieselben physischen Seiten auch in Prozess- und Compositor-Mappings vorkommen. Die Messung liefert keine vollständige GPU-Gesamtbilanz. [Linux-Kernel-Dokumentation zur DRM-Speicherstatistik](https://docs.kernel.org/gpu/drm-usage-stats.html)

## Übergänge und Ressourcen im Quellcode

### Bildcache und Dekodierung

1. **Das Pixelbudget greift.** Der statische LRU erfasst maximal 200 Einträge und 128 MiB für `Breite × Höhe × 4`, einschließlich gerade verwendeter Einträge. Ist keine Verdrängung möglich, bleibt das neue Bild außerhalb des Cache. Gleichzeitige Anforderungen teilen sich eine Operation für denselben Pfad und dieselbe DecodeWidth. Preloads treten dem gemeinsamen Ladevorgang bei und geben ihre Lease wieder ab. Normale Cache-Einträge bleiben nach der letzten Lease bis zur Verdrängung erhalten. Das erklärt bis zu 128 MiB geschätzter Pixelbytes im Gesamtprozess, maximal rund 8 % der 1.582,2 MiB zusätzlichen PSS nach dem produktiven BigMode-Lauf, selbst wenn alle 128 MiB resident und zusätzlich wären. [AsyncImageHelper.cs](../Helpers/AsyncImageHelper.cs#L595) · [AsyncImageHelper.Shared.cs](../Helpers/AsyncImageHelper.Shared.cs#L46) · [BigModeViewModel.cs](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.cs#L969)
2. **Gleichzeitige Decodes sind begrenzt.** BigMode erbt die serielle Decode-Lane über den Host, der Desktop kann bis zu vier Decodes ausführen. Die Quell- und Zielgrenze von 32 MiPixel pro Bild lässt bei BGRA bis zu 128 MiB für eine einzelne Pixelkopie zu. Avalonia/Skia kann für `DecodeToWidth` einen größeren Zwischenschritt dekodieren und beim Resize kurz Ausgangs- und Zielbitmap halten. HTTP puffert pro Bild bis 32 MiB kodierte Daten vor der Decode-Lane. Mehrere Downloads oder wartende Bodies können gleichzeitig leben. HTTP ist bei einer ausschließlich lokalen Bibliothek von nachrangiger Priorität. [AsyncImageHelper.cs](../Helpers/AsyncImageHelper.cs#L31) · [Downloadpfad](../Helpers/AsyncImageHelper.cs#L278) · [Bitmap-Decode](../Helpers/AsyncImageHelper.cs#L428) · [Avalonia 12.1.6 ImmutableBitmap](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Skia/Avalonia.Skia/ImmutableBitmap.cs#L59)
3. **Bilder können nach der Blende verborgen weiterleben.** `CrossfadeImage` storniert überholte Ladevorgänge und behält fertig dekodierte Bilder über Generationen hinweg. Nach abgeschlossener Blende schaltet `SetActiveVisibility` das ausgehende Bild unsichtbar. Die Quelle und die Lease bleiben bis zum Ersetzen, expliziten Leeren oder Detach erhalten. Bei `DisableCache=True` liegen beide Bitmaps außerhalb des globalen Budgets. Ein zusätzlicher 1.920 × 1.080-Puffer entspricht rund 7,9 MiB Pixeln je betroffener Ebene. Prism nutzt drei solche Wallpaper-Ebenen, deren Sichtbarkeit und Quellen variieren. Theme-Assets über den separaten Converter werden nativ in voller Größe geladen. Dessen 64 schwache Referenzen begrenzen die Eintragszahl. Die geladenen Views bestimmen die Lebensdauer der Bitmaps. [CrossfadeImage.cs](../Extensions/CrossfadeImage.cs#L232) · [Fade-Ende](../Extensions/CrossfadeImage.cs#L590) · [Detach und Lease-Freigabe](../Helpers/AsyncImageHelper.cs#L768) · [Prism](../Themes/Prism/theme.axaml#L108) · [ThemeAssetToBitmapConverter.cs](../Helpers/ThemeAssetToBitmapConverter.cs#L20)

### Gemeinsame Medienpipeline und Video

1. **Die Themes teilen sich die Vorschau.** Ein BigMode-Host setzt denselben A/B-Videocontrol in den Slot des Root- oder Systemthemes. Pro ViewModel werden zwei LibVLC-Instanzen und drei Player einschließlich eines optionalen Hintergrundkanals angelegt. Das passiert bereits beim Start ohne passende Videos. Callback-Puffer und `WriteableBitmap` entstehen beim ersten Frame. Die UI fasst anstehende Frame-Kopien zusammen. Bereits aktive Vorschauen für denselben effektiven Pfad werden beim Scrollen weiterverwendet. Die zeitweise Überlappung zweier unterschiedlicher Videos ist Teil der Blende. Pro aktiver Oberfläche sind Callback-Puffer und Avalonia-Bitmap zusammen nominal 15,8 MiB bei 1080p und 63,3 MiB bei 4K. Darin fehlen zusätzliche Decoder-Referenzbilder, Skia-Ressourcen, Treiberpuffer und etwaige Zwischenkopien. [Host-Slots](../Views/BigModeHostView.axaml.cs#L718) · [Playeraufbau](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.cs#L611) · [Framepuffer](../Helpers/Video/LibVlcVideoSurface.cs#L51) · [WriteableBitmap](../Helpers/Video/VideoSurfaceControl.cs#L90) · [VLC-Callback-Kosten](https://github.com/videolan/vlc/blob/3.0.x/include/vlc/libvlc_media_player.h#L352)
2. **Bestätigter Freigabefehler bei Medienreferenzen.** `ReferenceEquals(player.Media, media)` vergleicht den bestehenden Wrapper mit einem neu erzeugten Wrapper. Bei einem nicht leeren Getter erhält der Aufrufer jedes Mal eine native Referenz. Der neue Wrapper wird weder gespeichert noch explizit entsorgt. LibVLCSharp 3.10.2 gibt diese Referenz über `Dispose` frei. Der Vergleich verhindert auch das beabsichtigte Abhängen über `player.Media = null`. Die Stelle wird beim Entsorgen ausstehender Medien erreicht, sofern ein Player existiert. Das bestätigt verlorene native Referenzen im Code. Ein `Media`-Deskriptor und ein laufender Decoder haben unterschiedliche Lebenszyklen. Der Befund belegt daher keinen verlorenen vollständigen Decoder oder eine bestimmte PSS-Größe. [BigModeViewModel.Preview.cs](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Preview.cs#L752) · [LibVLCSharp Media-Getter](https://github.com/videolan/libvlcsharp/blob/3.10.2/src/LibVLCSharp/Shared/MediaPlayer.cs#L541) · [LibVLCSharp Dispose](https://github.com/videolan/libvlcsharp/blob/3.10.2/src/LibVLCSharp/Shared/Internal.cs#L38) · [Nativer Getter](https://github.com/videolan/vlc/blob/3.0.x/lib/media_player.c#L849)
3. **Gestoppter Player und versteckte Bitmap sind getrennt.** Die alte Wiedergabe stoppt nach dem ersten neuen Frame und Ablauf der Blende, sofern die Generation noch aktuell ist. Der native Callback-Puffer wird beim VLC-Cleanup oder beim Dispose der Surface freigegeben. Die UI-Bitmap wird bei Dimensionswechsel, Änderung der Surface oder beim Detach entsorgt. Ein bloßer Wechsel des aktiven Index setzt die Deckkraft der Video-Controls. Dadurch kann A oder B bei weiter angehängtem Control seine letzte Bitmap nach dem Stop behalten. Für einen ausgeblendeten 1080p-Slot sind das nominal 7,9 MiB, für einen 4K-Slot 31,6 MiB. Die Begrenzung auf A/B und einen optionalen Hintergrundslot begrenzt die direkte Wirkung dieser Aufbewahrung. [Vorschauwechsel](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Preview.cs#L233) · [VLC-Cleanup](../Helpers/Video/LibVlcVideoSurface.cs#L119) · [Video-Crossfade](../Helpers/Video/CrossfadeVideoSurfaceControl.cs#L164) · [Bitmap-Freigabe](../Helpers/Video/VideoSurfaceControl.cs#L80)
4. **Stop und vollständige Freigabe sind unterschiedlich terminiert.** `DesktopBigModeSession` wartet auf die Vorbereitung zum Detach, entfernt den Host und ruft `Dispose` am ViewModel auf. Das ViewModel beendet Timer, Beobachter und Event-Abos und startet den restlichen VLC-Abbau in einem unbeobachteten `Task.Run`. Ein sofortiger Neustart kann altes und neues VLC-Inventar überlappen. Wenn `Stop` oder `Media = null` im selben `try` scheitert, wird das folgende Player-Dispose übersprungen. Das ist ein konkreter Fehlerpfad, dessen Auftreten im produktiven Lauf unbestätigt ist. Die ausstehende Medienliste wird über `Stopped`, Leerlauf beim nächsten Start und abschließendes Dispose geleert. Der Stop-Pfad kann das Medium erst nach dem `Stopped`-Callback in die Liste eintragen. Deshalb Reihenfolge und Tiefe prüfen, insbesondere bei Wiederverwendung und Stop-Fehlern. [Sitzungsende](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/DesktopBigModeSession.cs#L86) · [Player-Abbau](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Navigation.cs#L81) · [Pending-Medien](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Preview.cs#L724) · [StopPreviewPlayer](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Preview.cs#L1390)
5. **AttractMode erhöht den Durchsatz.** Ein Spin stoppt die Vorschau, wählt bis zu 25 Elemente mit 120 ms Abstand und setzt anschließend eine zufällige Auswahl. Im AppImage markiert `StopVideo` den Hauptplayer zur Neuerzeugung beim nächsten Wiedergabestart, um einen Audiofehler zu umgehen. Attract kann dadurch wiederholt Decoder, Puffer und Player wechseln. Eine Änderung an dieser Sequenz muss den Audio-Workaround und den ersten sichtbaren Frame prüfen. Die Generationen und das Beenden des Timers verhindern regulär eine fortgesetzte Attract-Navigation nach dem Schließen. [AttractMode](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Attract.cs#L132) · [AppImage-Workaround](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Preview.cs#L1217)

### Themes, Navigation und Desktop

- **Host und Themes.** Detach stoppt den Maus-Timer, bricht Akzentarbeit ab und entfernt VM-Abos und Theme-Tuning. Die statische XAML-LRU hält bis zu 40 Texte bei je höchstens 1 MiB kodierter Dateigröße. Die System-LRU hält bis zu 20 `Theme`-Objekte je Host, einschließlich des geparsten `Theme.View`. Die sichtbare Systemansicht entsteht zusätzlich aus der Factory. Beim Wechsel überlappen alter und neuer Teilbaum für 180 ms. Avalonia 12.1.6 generiert bei jedem Runtime-Parse neue Typen in einem statisch gehaltenen dynamischen Modul. Das kann über viele Theme-Wechsel wachsen, selbst wenn alte Views eingesammelt werden. Die Assembly-Anzahl allein erfasst die zusätzlichen Typen im selben Modul nicht. Relevanz in einem langen Attract-Lauf innerhalb eines einzelnen Themes ist geringer, getrennte Wechseltests sind offen. [Host-Detach](../Views/BigModeHostView.axaml.cs#L175) · [Systemwechsel](../Views/BigModeHostView.axaml.cs#L994) · [Subtheme-Cache](../Views/BigModeHostView.axaml.cs#L1029) · [ThemeLoader.cs](../Services/ThemeLoader.cs#L53) · [Avalonia Runtime Compiler](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Markup/Avalonia.Markup.Xaml.Loader/AvaloniaXamlIlRuntimeCompiler.cs#L34)
- **Auswahlindizes und Preloads.** Der BigMode erstellt einen Index aus den bestehenden Bibliotheksmodellen. Auswahl und Scrollen nutzen die vorhandenen Positionen, kleine bedarfsgesteuerte Carousel-Fenster und abgebrochene Lookahead-Batches. Die positiven Pfad-Caches sind auf 10.000 Item- und 2.000 Node-Einträge ausgelegt und halten Pfade als Strings. Der Snapshot und die Projektionen kosten Speicher proportional zur Bibliothek. Für 5.000 Titel wären selbst mehrere zusätzliche Referenzarrays mit je 5.000 Referenzen auf x64 jeweils nur rund 39 KiB. Die tatsächlichen Navigationseinträge, Dictionaries und Strings kommen hinzu. Diese Strukturen erklären einen Teil des Startaufwands. Für ihren Anteil am Dauerspeicher braucht es einen Heap-Vergleich. [BigModeLibrarySnapshot.cs](../Services/BigMode/BigModeLibrarySnapshot.cs) · [Auswahlprojektion](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Library.cs#L176) · [Pfad-Caches](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.Preview.cs#L48) · [Preloads](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/BigModeViewModel.cs#L969)
- **Desktop bleibt der darunterliegende Teilbaum.** Beim Öffnen von BigMode wird ein Overlay in die vorhandene Desktop-Window eingesetzt. Desktop-VM, Bibliothek und die bestehende Desktop-Präsentation bleiben so im Prozess. Das Cover-Grid virtualisiert Zeilen bereits, baut aber kleine Projektionen für den gesamten sichtbaren Filterbestand. Das Desktop-Wallpaper verwendet selbst ein uncachebares CrossfadeImage. Die Auswahlwiederherstellung kann anschließend Detailbilder laden. Beim Vergleich daher dieselbe Desktop-Auswahl am Anfang und Ende messen oder den jeweiligen Zustand protokollieren. [Desktop-Wallpaper](../Views/MainWindow.axaml#L58) · [Overlay](../Views/MainWindow.axaml#L490) · [Virtualisierung](../Views/MediaAreaView.axaml#L305) · [Zeilenprojektion](../ViewModels/MediaAreaViewModel.cs#L457) · [Auswahlwiederherstellung](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/MainWindowViewModel.Command.cs#L506)
- **Desktop-Start hat eine messbare Quelle für temporäre Arbeit.** Das Bibliotheks-JSON wird aus einem Stream deserialisiert. Nach der ersten sichtbaren Ansicht scannt der Warmup die Medien erneut. `RescanNodeRecursive` ruft `ScanItemAssets` einzeln auf. Diese Methode nutzt intern bereits den Batch-Scanner, der für jeden Aufruf sämtliche relevanten Typordner des Knotens erneut enumeriert und Dateinamen prüft. Für einen Knoten mit N Titeln und M Asset-Dateien kann das N × M Dateieinträge verarbeiten. Der Code hält außerdem die Ergebnisse zur UI-Übernahme. Ein tatsächlicher Batch pro Knoten verdient eine getrennte Messung von Allokationsrate, Startspitze und Warmup-Zeit. [Stream-Deserialisierung](../Services/MediaDataService.cs#L326) · [Warmup](https://github.com/Dark574/Retromind/blob/12667a3ac1c0d53ae115a295aa659ca402b1acf0/ViewModels/MainWindowViewModel.cs#L660) · [Einzelaufrufe](../ViewModels/MainWindowViewModel.Import.cs#L849) · [Batch-Scanner](../Services/FileManagementService.cs#L250)

## Priorisierte Umsetzungsetappen

Die Bandbreiten sind Größenordnungen für die jeweils genannte Komponente. Mehrere Maßnahmen betreffen dieselben Bytes. Jede Etappe hat einen eigenen Vergleichslauf und kann separat bewertet werden.

| Etappe | Änderung zur Prüfung | Geschätztes Potenzial | Abnahme und Risiko |
|---|---|---|---|
| **1. VLC-Eigentum und Abbau · umgesetzt** | Temporären `Media`-Wrapper deterministisch entsorgen und den nativen Handle des auf dem Slot installierten Mediums prüfen oder die Slot-Zuordnung ohne Getter verfolgen. Ausstehende Medien in der Stop-Reihenfolge zuverlässig abräumen. Danach das Ende des vollständigen Abbaus beobachtbar machen und Player trotz Fehlern beim Stop bzw. Detach soweit sicher entsorgen. | Eine native Referenz pro betroffenem Getter entfällt. Für reine Deskriptoren als erste Arbeitshypothese einzelne MiB pro Tausend Wechsel. Ein *zusätzlich* hängen gebliebener Video-Slot kann bei 1080p schon mit 16 MiB App-Puffern plus Decoder deutlich mehr kosten. Die Größen der Deskriptoren und die Ausführung des Ausnahmewegs sind bisher ungemessen. | Ein Test muss den richtigen nativen Handle freigeben und einen inzwischen gestarteten Player schützen. Native Player-Zuweisungen sollten außerhalb des VLC-Callbacks ablaufen, da der Setter den Input stoppen und sperren kann. Stop-Fehler, verzögerter erster Frame, schnelles Wiederöffnen, gleiche Fallback-Datei und der AppImage-Audio-Workaround gehören zur Abnahme. Die UI darf auf den synchronen VLC-Stop nicht zusätzlich blockieren. |
| **2. Decoder-Allokationen und freien nativen Speicher eingrenzen** | Im stabilen Video-A/B-Lauf die größten aktiven und freigegebenen nativen Allokationen mit Stacks zuordnen. Bei dominanten Decoder-Frames zunächst eine Änderung am nachgewiesenen Verursacher testen, etwa gleichbleibende Wiedergabequalität bei weniger gleichzeitig angelegten Decoder-Puffern. Bei dominanten Skia-Uploads CPU- und GPU-Kopien gezielt verfolgen. | Die gemessene A/B-Differenz von 381–531 MiB in der ersten Sitzung gibt die Größenordnung der zusätzlichen Videolast vor. Der glibc-Lauf bestätigt mehrere hundert MiB freier Arena-Blöcke. Ein realer Gewinn in dieser Größenordnung setzt voraus, dass die Analyse vermeidbare Puffer oder wiederholte Allokationsspitzen findet. Für die Verteilung auf Decoder, Renderpfad und Allocator fehlen Stackdaten. VLC fordert beim Frame-Threading zusätzliches Bildinventar abhängig von der Threadzahl an. Avalonia erzeugt beim Zeichnen nach einer Bitmap-Änderung ein neues Skia-Snapshot-Objekt. Beide Stellen bieten konkrete Startpunkte für die native Spur. | 720p, 1080p, 4K, gleiche und wechselnde Auflösung, Software und VAAPI separat. Bildqualität, Frame-Pacing, Audio, Zeit zum ersten Frame und neue Spitzen beim A/B-Übergang messen. Keine globale GC-, Arena- oder Trim-Policy zur bloßen Senkung der PSS-Anzeige. |
| **3. Bild- und Video-Leases an sichtbare Lebensdauer binden** | Ausgehende Bild-Lease nach erfolgreicher Blende generationstreu lösen. Bei Video die letzte Bitmap nach bestätigtem Stop und Ablauf der Blende freigeben. Optionalen Hintergrundplayer bis zum ersten tatsächlich genutzten Slot verzögert anlegen, falls die Leerlaufmessung einen relevanten Betrag zeigt. | Bei 1080p ca. 8 MiB pro entfallender alter Großbild- oder Video-Bitmap. Bei mehreren aktiven uncached Ebenen grob 8–32 MiB, in 4K ca. 32 MiB pro Video-Bitmap. Ein Cache-Hit kann zu weiter gehaltenen, gemeinsam nutzbaren Pixeln führen. Das Potenzial eines unbenutzten VLC-Kanals vorab als Differenz messen. | Schnell zurück zum vorherigen Titel, fehlende Datei, überholte Fade-Fortsetzung, Themewechsel und Detach/Reattach prüfen. Identische Schärfe und Crossfade-Dauer sowie unveränderte Zeit zum ersten Frame sind Abnahmebedingungen. |
| **4. Bildspitzen und Desktop-Start** | Getrennt messen, welche Formate und Zielgrößen hohe Zwischenpuffer verursachen. Priorität für die aktuelle Auswahl vor obsoletem Lookahead beibehalten. Nur bei belegter HTTP-Last die Bytes wartender Bodies über alle Downloads budgetieren. Für Startup-Rescan den vorhandenen Batch pro Knoten prüfen und die Apply-Batches beibehalten. | Ein wegfallender 3.200 × 1.800-BGRA-Zwischenpuffer entspräche ca. 22 MiB, ein 32-MiPixel-Puffer ca. 128 MiB. HTTP maximal 32 MiB kodierte Bytes pro vermiedenem gleichzeitig wartendem Body plus nachweisbare Zwischenpuffer. Beim Rescan ist die Größenordnung für den PSS-Dauergewinn offen. Die Anzahl wiederholter Dateinamenprüfungen sinkt im Beispiel von N × M auf etwa N + M, was vor allem Startspitze und CPU betrifft. | Zielgröße an tatsächliche Pixelgröße und Skalierung koppeln, damit HiDPI-Schärfe erhalten bleibt. Derselbe Cover- und Theme-Mix muss ohne zusätzliche sichtbare Ladezeiten funktionieren. Beim Rescan Dateireihenfolge, alte Verzeichnisse, Asset-Abgleich, Abbruch und portable Pfade prüfen. |
| **5. Mehrfachwechsel der Themes und Desktop-Overlay** | Zuerst Live-Views, native Ressourcengrößen und emittierte XAML-Typen über wiederholte Wechsel zählen. Unbenutzte Prototype-Views im Systemcache nur bei nachgewiesenem Gewinn vermeiden. Desktop-Teilbaum im Overlay gesondert auf Ressourcen und Renderaufwand untersuchen. | Für einen Root-only-Attract-Lauf derzeit kein bezifferter Gewinn. Bei Systemthemes entspricht jede entfallene residente 4K-Pixelebene rund 32 MiB. Für XAML-Typen plus JIT nach 20–50 Wechseln, Desktop-Teilbaum und die Rückkehrlatenz ist ein A/B-Test nötig. | Live-Änderungen an portablen Themes, unabhängige Systemviews, erster sichtbarer Frame und Rückkehr zur gleichen Desktop-Scrollposition prüfen. Ein Theme-Wechsel darf keine zusätzliche Arbeit für die laufende Titelnavigation auslösen. |

Zwei Ansatzpunkte für die native Spur sind die zusätzlichen Bilder durch VLC-Frame-Threading und Avalonia-Snapshots nach aktualisierten Video-Bitmaps. Wie viele Bytes diese Pfade tatsächlich aktiv halten, hängt von Format, Ausgabe und Backend ab. [VLC avcodec](https://github.com/videolan/vlc/blob/3.0.x/modules/codec/avcodec/video.c#L503) · [Avalonia WriteableBitmapImpl](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Skia/Avalonia.Skia/WriteableBitmapImpl.cs#L110)

Ein pauschales Leeren des gesamten Bild-LRU könnte höchstens seinen aktuellen Bestand an geschätzten Pixelbytes entfernen und würde den warmen Wiedereinstieg verändern. Ein niedrigeres GPU-Cache-Limit kann zusätzliche Uploads auslösen. Für beides fehlt aktuell ein ausreichendes Verhältnis von Nutzen zu Risiko.

## Zielwerte auf dem produktiven System

| Zustand | Vorgabe | Produktive Differenz und Bewertung |
|---|---:|---|
| Desktop initial | ≤ 600 MiB | 530,6 MiB liegen 69,4 MiB unter dem Ziel. Weitere Arbeit am Desktop sollte zuerst Warmup-Spitzen und den geöffneten Ansichtsbestand messen. |
| BigMode im Dauerbetrieb | ≤ 1.024 MiB | Vom produktiven 30-Minuten-Wert fehlen 1.076,6 MiB. Vom Höchstwert fehlen 1.348,0 MiB. Dafür muss eine große Komponente der nativen Last oder ihrer Spitzen entfallen. Die begrenzten Bild- und Video-Fade-Puffer allein reichen in dieser Größenordnung nicht aus. |
| Desktop nach Verlassen | ≤ 800 MiB | Nach fünf Minuten fehlen 1.312,8 MiB. Das Ziel lässt gegenüber dem ursprünglichen Desktop 269,4 MiB zusätzliche PSS zu. Es ist erreichbar, wenn die nach dem Stop hoch bleibenden Seiten überwiegend aus vermeidbarer nativer Belastung oder zu lange gehaltenen Session-Ressourcen stammen. Diese Bedingung ist mit der produktiven Bibliothek offen. |

## Reproduzierbare Benchmarks und Regressionstests

1. **Reproduzierbare Baseline.** Pro Lauf AppImage-SHA, Commit und Build-Status, .NET und VLC, Host-glibc, Mesa/Treiber, Backend (X11/Xwayland und Wayland getrennt), Ausgabegröße, Skalierung und VLC-Hardwaremodus festhalten. Eine Kopie der produktiven Bibliothek mit identischer Kategorie- und Titelauswahl verwenden. Einen kleinen synthetischen Lauf daneben für schnellere Wiederholung behalten. Baseline dreimal aus frischem Prozess messen. Hook und Profiler in einem weiteren Lauf einzeln zuschalten, um deren Einfluss zu quantifizieren.
2. **Szenarien.** Desktop nach abgeschlossenem Warmup und mit identischer ausgewählter Kategorie sowie identischem Detailbild. BigMode mit Bild-only, mit Vorschau, mit Vorschau plus Hintergrundvideo und mindestens zwei repräsentativen Themes. Jeweils Idle, fester Pfadwechsel, wechselnde Auflösung und Attract. Erste Vorschau, Minute 5/12/30/60 und Peak pro Intervall aufzeichnen. Nach Rückkehr zum Desktop bei 0/30/300 s messen, dann den BigMode erneut öffnen. Zusätzlich 20 schnelle Öffnen/Schließen-Zyklen sowie Systemtheme-Wechsel mit derselben Medienfolge. Beim Lasttest frame- und zeitsensitive Übergänge separat von stabilen Zuständen betrachten.
3. **Erfassung.** Alle 2–5 s `smaps_rollup` und `System.Runtime`; volle `smaps` und DRM-`fdinfo` an Zustandsgrenzen. PSS, RSS, `Private_Dirty`, Swap, Gen 0/1/2/LOH/POH, Allokationsrate, GC-Häufigkeit und -Pausen erfassen. Parallel zur Speicherreihe aktive Theme-/Host-Instanzen, gepinnte/ungepinnte Cache-Bytes, uncached Pixel, Kontroll-Bitmaps, native VLC-Puffer, Player und ausstehende Medien zählen. Für Flugphasen Zeit bis zur sichtbar richtigen Auswahl, erstes korrektes Artwork, erstes Video-Frame, verworfene Frames, Frame-Zeiten P95/P99 und Rückkehrlatenz vergleichen.
4. **Objektgraph und native Spur.** An Desktop-Baseline, hoher Last und nach Desktop-Rückkehr getrennte Testläufe mit `dotnet-gcdump` oder `dotnet-dump` einplanen. `dumpheap -stat`, `eeheap -gc`, `gchandles` und `gcroot` auf alte Hosts, VMs, `BitmapLease`, Player und Assets anwenden. `gcdump` löst eine Gen-2-GC aus. Sein EventPipe-Puffer kann in der gemessenen Anwendung wachsen. Ein Prozessdump kann viele Seiten resident machen. Diese Zahlen eignen sich zur Ursachenanalyse außerhalb der PSS-Baseline. Tool und Ziel brauchen unter Linux denselben Benutzer und `TMPDIR`. Für `dotnet-dump` zunächst die Verfügbarkeit der passenden 10.0.15-Dump-Komponenten und des DAC für das selbstenthaltende Paket prüfen. Im entpackten `usr/bin` liegen keine separaten Dateien `createdump` oder `libmscordaccore.so`. Attach mit `dotnet-counters` ist bestätigt. `gcdump` und `dump` wurden in dieser Untersuchung nicht installiert oder ausgeführt. Für Native Heaptrack mit Symbolen für VLC/FFmpeg/Skia in demselben Szenario ausführen. Recorder und Dumps außerhalb der normalen PSS-Vergleichswerte halten. [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters) · [dotnet-gcdump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump) · [dotnet-dump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump) · [Heaptrack](https://github.com/KDE/heaptrack)
5. **Gezielte automatisierte Regressionen.** Bestehende Image-, BigMode- und Lifecycle-Tests um den jeweils geänderten Vertrag ergänzen. Für Video zählen zwei Wrapper über demselben nativen Handle, Stop vor und nach dem `Stopped`-Callback, ein Stop-/Detach-Fehler, sofortiger Session-Wechsel und ein verspäteter Callback vom alten Player. Für Bilder zählen überholte Generationen, Fade-Ende, Cache-Pins, Umschalten auf denselben Pfad und Dispose genau einmal. Für Themes zählen unabhängige View-Instanzen, erneutes Laden geänderter Dateien und nur ein aktiver Video-Slot je Kanal. Absolute PSS-Grenzen sollten bis zu einer stabilen Hardware-Baseline als Benchmark gelten. Für CI kann der Test zunächst die Ressourcenzähler und ihre Stabilisierung deterministisch prüfen.

Für eine laufende Reproduktion wird die PID des eigentlichen Retromind-Prozesses verwendet. Beim FUSE-AppImage kann zusätzlich ein AppImage-/Mount-Prozess existieren.

```bash
awk '/^(Rss|Pss|Pss_Anon|Pss_File|Pss_Shmem|Private_Dirty|SwapPss):/' \
    "/proc/$pid/smaps_rollup"
dotnet-counters collect -p "$pid" --refresh-interval 2 \
    --counters System.Runtime --format csv -o runtime.csv
```

## Validierung der Ausgangsanalyse

- `dotnet build Retromind.csproj --no-restore` erfolgreich mit 0 Warnungen und 0 Fehlern.
- Gefilterter `dotnet test` für AsyncImage, CrossfadeImage, BigMode-Medienpipeline, Videodiagnostik, Sitzungs- und Eingabe-Lebenszyklus, Auswahl und Theme-Switcher mit 111 bestandenen Tests. VSTest benötigte einen lokalen Socket und wurde dafür außerhalb der Sandbox ausgeführt.
- Der Kontrolllauf umfasst Video-, Bild- und Theme-Vergleiche auf Xwayland sowie einen ergänzenden Allocator-Lauf im Testprofil. Hardware-Decoding, native Wayland-Messung, Aufnahme der 30–60-Minuten-Kurve unter produktiver Last, tiefere native Allokationsstacks und Heap-Roots sind offen.
- Die Rohdaten und der temporäre Messaufbau liegen unter `/tmp/retromind-memory.lGOpHe`. Die Ausgangsanalyse änderte ausschließlich diesen Bericht.

## Umsetzung und Messung von Etappe 1

### Verhalten nach der Änderung

- Jeder A/B- und Hintergrundslot hält genau den Managed-`Media`-Wrapper, den Retromind besitzt. Der Player hält seinen eigenen nativen Retain. Beim Ende der Vorschau nimmt der Slot seinen Wrapper aus der aktiven Zuordnung, stoppt den Player synchron wie bisher, hängt das Medium nach dem Stop ab und entsorgt den Wrapper. Der aufrufende Code verwendet dafür keinen Getter und keine `Stopped`-Warteliste. Die Reihenfolge oder das Ausbleiben des Ereignisses beeinflusst die Freigabe des Wrappers damit nicht. Ein vorbereiteter Hintergrundclip kann während Home oder eines Spiels weiterhin für Resume am Player bleiben. [Slot-Eigentum und Freigabe](../ViewModels/BigModeViewModel.Preview.cs#L704) · [VLC-Player-Freigabeschritte](../Helpers/Video/VlcPlayerCleanup.cs#L9)
- Beim Schließen wird zuerst die aktuelle Auswahl einschließlich ausstehender Bibliotheksänderungen gespeichert. Danach werden die laufenden UI- und Vorschaugenerationen ungültig. Ein Worker übernimmt Stop, Detach und Dispose für alle drei Player, die Medien, Frameoberflächen und LibVLC-Instanzen. Stop, Detach, Player-Dispose und Wrapper-Dispose werden unabhängig versucht. Fehler bleiben über die vorhandene Videodiagnostik sichtbar. Der Desktop wartet asynchron auf das Ende der Ressourcenfreigabe, bevor der Host abgehängt oder eine neue Sitzung erzeugt wird. Das reguläre Schließen des Anwendungsfensters wartet auf denselben Ablauf. [VM-Abbau](../ViewModels/BigModeViewModel.Navigation.cs#L11) · [Desktop-Sitzung](../ViewModels/DesktopBigModeSession.cs#L84)
- Verzögerte Stops prüfen die Generation beim Ausführen auf dem UI-Thread. Queued Replay- und Frame-Callbacks prüfen die aktuelle Player-Zuordnung bzw. Generation vor dem Zugriff. Die AppImage-Regel erzeugt nach `StopVideo` weiterhin einen frischen Hauptplayer für den nächsten Vorschau-Start. Codec-, Ausgabe- und Bildcache-Einstellungen sowie Crossfade-Dauern folgen weiterhin den bestehenden Einstellungen. [Verspätete Stops](../ViewModels/BigModeViewModel.Preview.cs#L1481) · [Callback-Gültigkeit](../ViewModels/BigModeViewModel.VideoDiagnostics.cs#L49)

### Vorher-/Nachher-Messung

Verglichen wurden zwei Release-Builds desselben Checkouts vor und nach Etappe 1. Beide liefen mit Host-.NET 10.0.14, den LibVLC-3.0.24-Bibliotheken des entpackten AppImage und dessen `APPIMAGE`-Betriebsmodus. Die virtuelle Xwayland-Ausgabe lief mit 1.920 × 1.080 auf der AMD Radeon 8060S. Pro frischem Prozess gab es einen separaten portablen Datenordner. Bibliotheksdaten, Navigator-XAML, Bild- und Videodateien waren identisch. Verwendet wurden die 5.000 synthetischen Titel der Ausgangsanalyse, davon 256 mit Medien in der aktiven Kategorie, und dieselben 1080p-H.264-Clips.

Der Messablauf wartete fünf Sekunden am Desktop, öffnete bei Titel 0000 und wechselte dann sequenziell über 0001 bis 0012. Nach der ersten passenden Videoframe-Revision wartete er jeweils 650 ms. Zwei Sekunden nach dem letzten Wechsel folgte ein Schließen-/Öffnen-Versuch im selben UI-Aufruf. Danach folgten nach abgeschlossenem Abbau zwei weitere Öffnen-/Schließen-Zyklen. Retromind-Commands und erwartete Frames wurden über einen temporären Startup-Hook beobachtet. `smaps_rollup` wurde ungefähr alle 100 ms gelesen, Ressourcenzähler ungefähr alle 50 ms. Die Zähler sind Stichproben und können kurze Übergänge verfehlen. Pro Phase ging der Median der letzten bis zu zehn Stichproben in den Vergleich ein. Der BigMode-Peak ist die höchste Stichprobe der ersten Sitzung.

Die Tabelle zeigt den Median aus **fünf frischen Prozessen je Build** und in Klammern die Spannweite. Die Differenzen sind vor Rundung berechnet. Die beiden Messreihen liefen nacheinander mit derselben virtuellen Displaykonfiguration.

| PSS in MiB | Vorher | Nachher | Mediandifferenz |
|---|---:|---:|---:|
| Desktop vor BigMode | 207,9 (204,0–209,4) | 203,9 (200,3–208,7) | −4,0 |
| Nach zwölf Videowechseln | 1.149,5 (915,1–1.236,1) | 1.045,2 (941,0–1.058,6) | −104,2 |
| Peak der ersten BigMode-Sitzung | 1.257,5 (1.031,5–1.343,8) | 1.153,3 (1.056,5–1.251,9) | −104,2 |
| Desktop nach dem ersten Schließen | 1.049,6 (811,2–1.129,9) | 914,8 (802,8–938,1) | −134,8 |
| Desktop nach drei Öffnen-/Schließen-Zyklen | 1.164,7 (879,4–1.272,2) | 1.010,5 (851,9–1.098,8) | −154,2 |

Die Mediane liegen im Kontrolllauf niedriger. Die Spannweiten überlappen deutlich. Daraus lässt sich keine belastbare Einsparung für den produktiven Dauertest ableiten. Etappe 1 zeigt unabhängig davon, dass die ausstehenden Medien entfallen und der Abbau abgeschlossen ist, bevor eine neue Sitzung startet. Vom beobachteten Auswahlwechsel bis zur ersten passenden Frame-Revision lagen beide Fassungen bei **251 ms im Median und 301 ms im 95. Perzentil** über je 60 Wechsel. Die Auflösung dieser Messung beträgt etwa 50 ms. Physische Eingabelatenz und Bildgleichmäßigkeit während der Wiedergabe wurden damit nicht erfasst.

Bei der Ressourcenprüfung ließen sich in beiden Fassungen je Sitzung höchstens drei noch gültige Player-Wrapper beobachten. Zwei Medien und zwei native Callback-Puffer überlappten während der Blende. Die beiden Puffer enthielten zusammen höchstens 16.742.400 Bytes, rund 16 MiB. **Die ausstehende Medienliste ging von maximal 1 auf 0**. Am letzten Desktop-Messpunkt waren je Lauf **0 gültige Player-Wrapper, 0 zugeordnete oder ausstehende Medien und 0 native Callback-Puffer** beobachtbar. Die Zählung erfasst Anwendungsobjekte und ihre Frameoberflächen. Verlorene native Deskriptor-Referenzen aus dem früheren ungesicherten Getter, VLC-interne Decoderbilder, WriteableBitmaps, GPU-Ressourcen und freie Allokatorblöcke sind damit außerhalb der Zählung.

Ein weiterer A/B-Funktionstest verwendete einen gemeinsamen Fallback für die ersten zwei Titel und eine eigene Videodatei für den dritten. Die Frame-Revision blieb beim gemeinsamen Pfad bei 1 und stieg mit dem eigenen Video auf 2. Nach einem echten Attract-Spin nahm sie in beiden Builds auf 3 zu. In beiden Builds wurde genau ein weiterer Player erzeugt, und die nächste Vorschau meldete Audio-Track 1 sowie die normale Lautstärke 100. Dieser Test verwendete einen stillen AAC-Track und eine virtuelle ALSA-Ausgabe. Ein echter Audioausgang und gerätespezifisches Frame-Pacing bleiben offen.

Die Build-Ausgaben und Messskripte sowie die CSV-, Zustands- und Funktionstest-Protokolle liegen unter `/tmp/retromind-vlc-stage1`. Der Ausgangsbuild stammt von `12667a3`. Beide Fassungen wurden mit `dotnet build Retromind.csproj -c Release --no-restore -o <Ausgabeordner>` erstellt. Die Messung nutzt den AppImage-VLC-Payload und einen frameworkabhängigen Release-Build zum exakten Quellvergleich. FUSE-Start, native Wayland- und Hardwaredecode-Pfade, Hintergrundvideo im Dauertest und die ursprüngliche produktive Bibliothek bleiben für den Vorher-/Nachher-Vergleich offen.

### Regressionen und verbleibende Risiken

- Die gezielten Tests prüfen zwei Managed-Wrapper über derselben nativen Identität, synchrones, verspätetes oder fehlendes `Stopped`, Stop-/Detach-Fehler mit weiter ausgeführtem Dispose, alte Frame-/Replay-Generationen und den AppImage-Recreate-Pfad. Ein kontrolliert blockierter nativer Pufferabbau zeigt, dass der UI-Dispatcher weiterläuft, der Fenster-Shutdown denselben Abbau abwartet und der nächste Öffnungsversuch erst nach dem Abschluss einen neuen Host anlegt. Der Test prüft auch die Auswahl, wenn der vorher ausgewählte Titel unmittelbar vor dem Schließen aus der Bibliothek entfernt wurde. Bestehende Tests prüfen den gemeinsamen Videopfad sowie den Wechsel zu einer eigenen Videodatei. [Ownership-Tests](../tests/Retromind.Tests/ViewModels/BigModeVlcOwnershipTests.cs) · [Desktop-Sitzungstests](../tests/Retromind.Tests/ViewModels/DesktopBigModeSessionTests.cs)
- Ein synchroner VLC-Stop, der in VLC selbst dauerhaft hängen bleibt, kann den Abschluss der Sitzung weiterhin verzögern. Der Worker kann einen nativen Prozessfehler nicht abbrechen. Falls Stop, Detach und Player-Dispose auf einem Slot alle fehlschlagen, lässt Retromind den Framepuffer bei den VLC-Callbacks, um eine Freigabe während möglicher nativer Zugriffe zu vermeiden. Ein dauerhaft hängender oder fehlerhafter Treiber bleibt ein Fehlerpfad außerhalb des normalen Speichertests.
- Für Etappe 2 zuerst die anhaltend hohe PSS nach dem Abbau der Anwendungsressourcen mit zeitgleichen nativen Allokationsstacks sowie glibc-Arenastatistik untersuchen. Der erste Vergleich sollte denselben H.264-Clip-Mix und feste Wechsel verwenden, danach die produktiven Formate und Auflösungswechsel. Relevante Kandidaten sind Decoder-/Vout-Pools, Bitmap-Snapshots und Allokator-Wiederverwendung. Beim Vergleich zusätzlich Frame-Zeit und Audioausgabe am physischen Testgerät prüfen.

### Validierung der Umsetzung

- `dotnet build Retromind.csproj --no-restore` sowie beide Release-Builds erfolgreich mit 0 Warnungen und 0 Fehlern.
- `dotnet test tests/Retromind.Tests/Retromind.Tests.csproj --no-restore` erfolgreich mit **2.551 bestandenen Tests**, 0 fehlgeschlagen und 0 übersprungen. Der Testlauf hat sein TRX-Protokoll im temporären Messordner abgelegt.
- `git diff --check` erfolgreich. Die zwei A/V-Funktionstests und die zehn aufgenommenen Speicherläufe erreichten ihre erwarteten Endzustände.


## Etappe 2 – diagnostischer Abschluss und offener Stabilitätsbefund

Etappe 2 gilt gemäß gemeinsamer Bewertung als diagnostisch abgeschlossen. Die
experimentelle Begrenzung der Decoderthreads und die Variante ohne wiederkehrende
Player-Neuerzeugung sind nicht Bestandteil des Produktivstands. Der AppImage-Audio-
Workaround bleibt erhalten. Die hier erhaltene Berichtskopie enthielt noch keinen
vollständigen Etappe-2-Abschluss; deshalb werden im Folgenden ausschließlich anhand
der vorhandenen Rohdaten überprüfbare Befunde übernommen.

Die disjunkt nach Allokationspfaden ausgewertete Heaptrack-Spur
`/tmp/retromind-stage2/trace-1080/video-costs.txt` meldet am Video-Messpunkt:

| Allokationspfad | Kumulativ allokiert, MiB | Höchster aktiver Bestand des Pfads, MiB | Am Messpunkt aktiv, MiB |
|---|---:|---:|---:|
| VLC `picture_NewFromFormat` | 2.118,0 | 277,8 | 138,9 |
| Skia | 640,7 | 71,7 | 49,5 |
| FFmpeg | 515,3 | 57,9 | 29,2 |
| VLC Streamcache | 156,0 | 24,0 | 12,0 |
| Mesa/LLVM | 204,0 | 17,8 | 16,9 |

Diese Kategorien werden durch den ersten erkannten Pfad zugeordnet; pro Allokation
wird nur eine Kategorie gezählt. Peaks verschiedener Kategorien sind nicht zeitgleich
und dürfen nicht zu einem Gesamtpeak addiert werden. Kumulativ allokierte Bytes sind
weder lebender Speicher noch PSS. In der Attract-Spur waren nach vollständigem Abbau
VLC-Pictures, FFmpeg und Streamcache bei 0 aktiven Bytes, während Skia 90,7 MiB und
Mesa/LLVM 17,0 MiB meldeten. Die Symbolisierung ist unvollständig. Insbesondere Skia
ist damit nicht vollständig in Bildcache, Videopräsentation, Snapshots und GPU-Uploads
zerlegt. Die Heaptrack-Spur ersetzt keine unveränderte PSS-Baseline.

Die damaligen allocatorbegleiteten Messungen zeigten beispielsweise nach der ersten
Rückkehr zum Desktop bei 1080p 838,5 MiB PSS und 839,4 MiB freie glibc-Arena-Blöcke;
bei 4K 2.161,3 MiB PSS und 2.178,3 MiB freie Blöcke. Das sind verschiedene Sichtweisen
auf teilweise dieselben Adressen, keine addierbaren Speicherposten. Die Zahl der
Anwendungsplayer und nativen Callback-Puffer war nach dem Abbau jeweils 0.

**Offen bleibt ein nativer Stabilitätsbefund:** Unter anderem endete der frühere
`base-attract`-Lauf mit Signal 11, obwohl kein Heaptrack aktiv war. Sein Hook führte
aber zusätzliche native Playerabfragen und Allocator-Abfragen aus. Eine Ursache ist
damit weder VLC noch Avalonia noch dem Profiler eindeutig zugeordnet. Ein erfolgreicher
Kontrolllauf ist kein Nachweis, dass der Absturz behoben ist.

## Etappe 3 – Bild- und Video-Leases an die sichtbare Lebensdauer binden

**Historischer, nicht freigegebener Stand B.** Der aktuelle Rückfallstand T und
seine Abnahmegrenzen stehen im folgenden Etappe-3b-Abschnitt.

### Änderung und Sicherheitsgrenzen

`CrossfadeImage` löscht nach abgeschlossener Blende die URL der ausgehenden internen
Bildebene über `AsyncImageHelper`. Damit endet genau diese Consumer-Lease. Bei
`DisableCache=True` wird die zugehörige Bitmap freigegeben; bei Cache-Bildern können
der bestehende LRU und andere Consumer sie weiterhin halten. Ladegeneration und
aktiver Slot werden vor der Freigabe geprüft. Zusätzlich müssen die effektiven
animierten Opazitäten vollständig bei 0 bzw. 1 angekommen sein. Der bisherige Timer
allein war bei einem zurückliegenden Animationsclock kein ausreichender Beleg.
Ein fehlgeschlagener oder sehr langsamer Ersatz gibt auch seinen vollständig
weggeblendeten Fallback frei. Überholtes Clear/Fade, Rückkehr während einer Blende,
identische URLs und gemeinsam verwendete Bitmaps bleiben voneinander getrennt.

Für Video gibt es einen kleinen verwalteten `VideoPlaybackLifetime`-Vertrag zwischen
Playerbesitzer und Präsentation. Er unterscheidet erwartetes Stoppen von bestätigter
Input-Ruhe nach dem bestehenden synchronen Stop-/Detach-/Dispose-Ablauf. Der
unveränderte `VlcPlayerCleanup` versucht diese Schritte weiterhin unabhängig.
Erst eine solche Bestätigung und ein vollständig ausgeblendeter bzw. unsichtbarer
Slot erlauben die Freigabe des letzten `WriteableBitmap`. Stop und Fade dürfen in
beliebiger Reihenfolge eintreffen. Ein alter Stop kann eine neue Playbackgeneration
nicht bestätigen. Queued Framekopien prüfen außerdem Surface- und Playbackgeneration.

Der UI-Thread besitzt weiterhin Kopie, Bitmaptausch und Dispose. Avalonia zeichnet
Bitmaps in seinem Renderdatenstrom mit einer eigenen Referenz auf die native
Plattform-Bitmap auf. Deshalb kann die UI ihre Referenz abgeben, während bereits
aufgezeichnete Renderarbeit noch läuft. Ein Regressionstest nutzt diesen tatsächlichen
Zeichenpfad und prüft die weiter gültige native Oberfläche nach Abgabe des UI-Besitzes.
[Bitmap-Besitz in Avalonia](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Avalonia.Base/Media/Imaging/Bitmap.cs#L81),
[Referenz im Renderdatenstrom](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Avalonia.Base/Rendering/Composition/Drawing/RenderDataDrawingContext.cs#L136).

Das Umhängen eines gemeinsamen Videocontrols bei Themewechseln invalidiert seine
alte Fade-Fortsetzung. Beim erneuten Anhängen wird die Freigabeprüfung für den
aktuellen Slot wieder aufgenommen. Ein lediglich gebundener, aber nie angehängter
Videoslot abonniert keine Frames und legt keine Präsentations-Bitmap an. Sichtbarkeitsabonnements werden beim Abhängen
entfernt. Native VLC-Callback-Allokation, Format-/Lock-/Copy-Protokoll, Medienbesitz,
asynchroner Sitzungsabbau, Decoderoptionen, Cachelimits und Preloads bleiben erhalten.

### Messverfahren

Vorher: unveränderter Release-Build von `d8fa12f`. Nachher: derselbe Quellstand mit
Etappe 3. Beide verwenden Host-.NET 10.0.14, das LibVLC-3.0.24-Payload des bisherigen
AppImage und den `APPIMAGE`-Betriebsmodus einschließlich Audio-Workaround. Virtuelle
KWin/Xwayland-Ausgabe: 2.560 × 1.440, Skalierung 1, etwa 60 Hz; AMD Radeon 8060S,
Mesa 26.3.1. Es gibt keinen Heaptrack und keine geänderte Allocator- oder GC-Policy.

Jeder frische Prozess bekommt eine eigene kopierte Bibliotheks-/Settings-Datei und
separate HOME-/XDG-Verzeichnisse. Die Bibliothek umfasst unverändert 5.000 synthetische
Titel mit 256 aktiven Medientiteln. Die Tests verwenden dieselben Fixture-Bilder und
H.264-Dateien wie die bisherigen Messungen: 720p, 1080p, 4K sowie zyklisch 720p/1080p/4K.
Ein separater Bildlauf verwendet das unveränderte Prism-Theme ohne Vorschauvideos,
damit dessen uncached Ebenen sichtbar werden. Die produktive Bibliothek wird nicht
beschrieben. Bereits vorhandene Messdateien unter `/tmp` werden nicht eingecheckt.

Pro Szenario laufen zunächst zwei frische Prozesse je Build, in der Reihenfolge
Vorher–Nachher–Nachher–Vorher. Nach fünf Sekunden Desktop folgt BigMode bei Titel 0000,
dann 0001 bis 0012. Nach der ersten passenden Frame-Revision bzw. Bildbereitschaft
wird 1,2 Sekunden gewartet; nach dem letzten Wechsel weitere zwei Sekunden. Danach
folgen ein Schließen-/Öffnen-Versuch im selben UI-Aufruf und nach vollständig beendetem
Abbau zwei weitere Sitzungen. Die Endphase am Desktop dauert drei Sekunden.

PSS wird etwa alle 100 ms aus `smaps_rollup` gelesen. Ein in beiden Builds identischer
Startup-Hook bedient normale Commands und beobachtet die Ressourcen ungefähr alle
50 ms. Er erfasst Consumer-Leases, unterschiedliche verwaltete Bitmapobjekte, deren
Pixelbytes und die nativen Callback-Flächen. glibc `malloc_info` läuft nur für vier
Checkpoints auf einem Messworker; jeweils werden auch volle `smaps` und DRM-Zähler
erfasst. Die XML-Datei wird erst nach Ende der Abfrage veröffentlicht. Es gibt keine
nativen AudioTrack-/Volume-Abfragen wie im alten Hook.

Die Zähler umfassen auch den unverändert angehängten Desktop hinter BigMode. Mehrere
Leases derselben Bitmap werden bei den Pixelbytes nur einmal gezählt. Cachebestand,
Leases und PSS sind nicht addierbar. Video-Callback- und Präsentationsflächen sind
verschiedene Speicherbereiche. Pixelbytes sind Untergrenzen der Anwendungsressourcen;
Skia-Zwischenobjekte, noch vom Renderer gehaltene Referenzen, GPU-Pools, Metadaten und
freie Allocatorblöcke sind darin nicht vollständig enthalten. Kurzzeitige Spitzen
unterhalb der Abtastung können fehlen. UI-Callbackintervalle sind keine gemessenen
physischen Display-Frametimes. Die künstliche Audioausgabe prüft keine hörbare Qualität.

Die Vorläufe `smoke-*`, `measure-*` und `final-*` sind getrennt und nicht Teil des abschließenden
Vergleichs. Die abschließenden `accepted-*`-Läufe dokumentieren die DLL-SHA jedes Prozesses.

### Zuordnung der CPU-Bitmap-Pixel

Eine statische IL-Prüfung der tatsächlich verwendeten `Avalonia.Base.dll` 12.1.6
zeigt: `Avalonia.Platform.Internal.UnmanagedBlob.Alloc` verwendet unter Linux direkt
`mmap`; `Free` verwendet `munmap`. Auf anderen Plattformen ist der Pfad
`Marshal.AllocHGlobal`/`FreeHGlobal`. Die Pixelfläche des Skia-WriteableBitmap wird im
vorhandenen Framework mit diesem Blob angelegt. **Diese direkten Linux-Mappings sind
nicht Teil der glibc-`malloc_info`-Arenen und auch nicht deren `total mmap`.**
Ein fehlender entsprechender Rückgang der glibc-Belegung widerlegt die Bitmap-Freigabe
somit nicht. Umgekehrt belegt ein gelöschtes `_bitmap`-Feld noch keinen Abschluss aller
Renderreferenzen. Der grafische Funktionslauf verfolgt deshalb zusätzlich die
verwalteten Skia-Plattform-Bitmapobjekte und deren Dispose-Zustand, ohne native
Pixelpointer auszulesen. Das ist kein globaler `malloc_trim` und keine Allokatoränderung:
Die bestehende Framework-Freigabe gibt die konkrete Ressource zurück.

Die reproduzierbare statische Prüfung und Ausgabe liegen unter
`/tmp/retromind-stage3/il` und `/tmp/retromind-stage3/unmanaged-blob-il.txt`.
Skia-Snapshots, Texturcaches und Treiberressourcen sind damit weiterhin nicht vollständig
bytegenau aufgeteilt. Sie werden nicht pauschal zu den Pixelbytes hinzuaddiert.

### Ergebnisse der abschließenden Messung

Werte in MiB PSS: Median der frischen Prozesse, in Klammern Minimum–Maximum.
Der Desktop-Endwert stammt aus der letzten Sekunde der dreisekündigen Ruhephase nach
drei Sitzungen. Der Peak bezieht sich auf die erste BigMode-Sitzung. Für 4K wurden
wegen der auffälligen Streuung drei zusätzliche interleavte Paare ergänzt, insgesamt
fünf Prozesse pro Build. Alle 26 gewerteten Prozesse endeten mit Exitcode 0; die
DLL-SHA jedes Prozesses stimmt mit dem Manifest überein.

| Szenario | Build | n | Ende der Auswahlfolge | BigMode-Peak | Desktop nach drei Sitzungen |
|---|---|---:|---:|---:|---:|
| 720p | Vorher | 2 | 786,1 (760,6–811,6) | 816,4 (813,7–819,2) | 740,8 (730,9–750,8) |
| 720p | Nachher | 2 | 713,9 (687,4–740,3) | 798,0 (790,6–805,4) | 769,1 (751,1–787,1) |
| 1080p | Vorher | 2 | 945,6 (822,0–1.069,2) | 1.127,9 (943,4–1.312,4) | 939,1 (749,1–1.129,1) |
| 1080p | Nachher | 2 | 850,4 (827,7–873,0) | 1.068,4 (1.036,2–1.100,5) | 840,8 (837,8–843,8) |
| 4K | Vorher | 5 | 1.712,9 (1.542,6–2.232,8) | 2.473,3 (2.247,9–2.751,7) | 1.272,5 (1.124,1–1.445,6) |
| 4K | Nachher | 5 | 2.157,9 (1.994,6–2.813,8) | 2.817,3 (2.461,6–3.271,1) | 1.805,8 (1.435,4–2.394,1) |
| 720p/1080p/4K | Vorher | 2 | 1.217,9 (1.134,2–1.301,6) | 1.603,1 (1.561,3–1.644,8) | 1.149,6 (1.107,9–1.191,3) |
| 720p/1080p/4K | Nachher | 2 | 943,6 (847,1–1.040,1) | 1.533,5 (1.440,6–1.626,4) | 1.088,0 (997,8–1.178,3) |
| Prism, nur Bilder | Vorher | 2 | 486,6 (479,4–493,9) | 525,0 (519,6–530,5) | 542,5 (526,1–558,8) |
| Prism, nur Bilder | Nachher | 2 | 469,2 (446,1–492,3) | 505,2 (492,3–518,2) | 503,0 (495,9–510,2) |

**Die 4K-PSS-Abnahme ist nicht bestanden.** Der Nachher-Median liegt am Ende der
Auswahlfolge um rund 445 MiB, nach der Rückkehr um rund 533 MiB höher. Das darf trotz
korrekt kleinerer aktiver Ressourcen nicht als Speicherverbesserung dargestellt
werden. Auch die größeren freien glibc-Blöcke sind realer Prozessverbrauch, solange
ihre Seiten resident bleiben. In den anderen Szenarien sind die Mediane überwiegend
kleiner, beim 720p-Desktop-Endwert jedoch ebenfalls größer. Kleine Stichproben und die
breiten Spannen erlauben keine pauschale positive PSS-Aussage.

### Tatsächlich gehaltene Ressourcen und Allokationsspitzen

| Stabiler Endpunkt | Videobitmaps vorher → nachher | Gehaltene Pixelbytes vorher → nachher | Freigegebener Bestand |
|---|---:|---:|---:|
| 720p | 2 → 1 | 7,21 → 3,60 MiB | 3,60 MiB |
| 1080p | 2 → 1 | 15,97 → 7,98 MiB | 7,98 MiB |
| 4K | 2 → 1 | 63,81 → 31,90 MiB | 31,90 MiB |
| Mix, zuletzt 720p, zuvor 4K | 2 → 1 | 35,51 → 3,60 MiB | 31,90 MiB |
| Prism ohne Video, uncached Bild-Leases | 4 → 3 | 19,92 → 12,01 MiB | 7,91 MiB |

Die Video-Flächen sind die tatsächlich ausgehandelten Ausgaben und nicht bloß die
nominalen Decoderabmessungen. Im Funktionslauf wurden beispielsweise 1.920 × 1.090
und 3.840 × 2.178 Pixel beobachtet. Bei den Navigator-Videoläufen bleiben 127 zugeordnete
Bild-Leases mit 21,26 MiB unterschiedlichen Bildpixeln in beiden Builds gleich.
Im Prism-Bildlauf sinken sie von 138 auf 137 und von 48,23 auf 40,32 MiB. Der globale
Cachebestand bleibt im jeweiligen Szenario identisch: 21,26 MiB bzw. 28,32 MiB.
Die Desktop-Kacheln hinter BigMode sind in diesen Zahlen enthalten.

Die notwendige Überlappung während der Blende bleibt erhalten. Maximal gehaltene
Videobitmap-Pixel betragen vorher und nachher 7,21 MiB bei 720p, 15,97 MiB bei 1080p,
63,81 MiB bei 4K und 39,89 MiB im Mix. Die native Callback-Fläche erreicht jeweils
dieselben separaten Maximalwerte, maximal zwei Puffer. Die uncached Bildspitze des
Prism-Laufs bleibt bei 19,92 MiB. Die Maßnahme reduziert also den stabil gehaltenen
Bestand; die zum Übergang und Dekodieren nötigen Spitzen entfallen nicht.

An jedem abschließenden Desktop-Endpunkt sind 0 aktive Player, 0 Medienzuordnungen,
0 ausstehende Medien, 0 native Callback-Puffer und 0 Videobitmaps vorhanden. Die noch
sichtbare Desktopdarstellung hält eine uncached Bitmap mit 7,91 MiB und ihre normalen
Cache-Leases. Diese sichtbare Desktopquelle wird nicht als Leck gezählt.

Eine Gegenprobe verfolgt bei realer Wiedergabe zusätzlich die Skia-Plattformobjekte.
Nach beschädigtem Video, Wechsel auf einen Titel ohne Video und Ablauf der Blende
halten beide Builds 0 Medien und 0 VLC-Puffer. Vorher bleiben jedoch zwei
WriteableBitmaps mit zusammen **39,89 MiB** und zwei noch lebende Plattform-Pixelflächen.
Nachher sind es **0 UI-Bitmaps und 0 lebende Plattform-Pixelflächen**; beide beobachteten
Plattformobjekte sind explizit disposed und noch nicht vom GC eingesammelt. Damit
ist dieser Gewinn keine Verwechslung zwischen einem leeren UI-Feld und weiter
gehaltenem Nativeigentum. Der Renderreferenz-Test deckt die sichere Überlappung mit
noch laufender Zeichenarbeit ab.

### glibc und die weiterhin hohe PSS

Ein besonders deutlicher Kontrollpunkt sind zwei 4K-Nachher-Prozesse mit demselben
aktiven Bestand:

| Ende Auswahlfolge | PSS | Arena-Größe | Freie Blöcke, fast + rest | glibc mmap | Arena minus frei plus mmap |
|---|---:|---:|---:|---:|---:|
| accepted-after-4k-1 | 1.995,3 | 2.020,8 | 1.369,0 | 100,8 | 752,7 |
| accepted-after-4k-2 | 2.813,8 | 3.008,3 | 2.356,5 | 100,8 | 752,7 |

Alle vier Checkpoints dieser Prozesse melden jeweils 32 glibc-Arenen. Die rechnerische
Belegung inklusive Verwaltungsdaten bleibt am Videoendpunkt bei 752,7 MiB, während
freie Arena-Blöcke und PSS stark auseinanderliegen. Nach dem letzten Schließen liegen
die entsprechenden rechnerischen Belegungen bei 106,9 und 107,1 MiB. Das belegt keine
seitengenaue PSS-Zuordnung und macht den höheren Verbrauch nicht harmlos; es grenzt ihn
jedoch deutlich gegen noch aktive Anwendungsplayer oder Callback-Puffer ab.
Die direkten Avalonia-Pixelmappings liegen außerhalb dieser glibc-Bilanz.

Eine relevante Änderung des Allokationsmusters ist zu erwarten: Bei gleichbleibender
Videoauflösung konnte der bisherige Renderer dieselben zwei WriteableBitmaps über
viele Titel wiederverwenden. Die sofortige Freigabe inaktiver Slots erfordert beim
nächsten Einsatz eine neue Bitmap. Für die 13 Titel einer 4K-Sitzung bedeutet der
Codepfad statt zwei initialen bis zu 13 Pixelanlagen, also grob 351 MiB zusätzliche
kumulativ angelegte Bitmap-Pixel. Das ist eine aus dem Lebenszyklus abgeleitete
Größenordnung, kein gemessener zusätzlicher Livebestand und kein Beweis für die Ursache
der gesamten PSS-Zunahme. Der kontrollierte Versuch mit beibehaltenen Slot-Bitmaps
wird separat ausgewiesen.

Die getrennte Gegenprobe `/tmp/retromind-stage3/retain-source` enthält alle
Etappe-3-Änderungen, überspringt aber ausschließlich die Freigabe eines noch
angehängten gestoppten Videobitmaps. Detach und Größenwechsel bleiben unverändert.
Sie ist **kein Produktivcode**. Zwei frische 4K-Prozesse melden am Videoendpunkt
1.987,0 und 1.955,8 MiB PSS, Peaks von 2.532,6 und 2.527,6 MiB und Desktop-Endwerte
von 1.871,3 und 1.499,1 MiB. Sie halten weiterhin zwei Bitmaps/63,81 MiB.
Gegenüber dem Sofortfreigabe-Stand fallen ihre Video- und Peakwerte günstiger aus;
die Werte erreichen jedoch nicht durchgehend die ursprüngliche Baseline, und die
Stichprobe ist kleiner. Das unterstützt die Priorität einer Prüfung von
Neuanlage/Wiederverwendung, beweist aber nicht, dass ausschließlich die zusätzliche
Bitmap-Allokation die komplette Arena- und PSS-Differenz verursacht. Der aktuelle
Sofortfreigabe-Stand bleibt deshalb ohne 4K-Performancefreigabe.


Ein ergänzendes Mix-Paar mit demselben Bedienplan, aber nur dem einfachen Bedienhook,
ohne Ressourceninventar und ohne `malloc_info`, ergibt 1.091,8 → 1.132,2 MiB am
Videoendpunkt und 1.156,5 → 1.225,6 MiB am Desktop-Endpunkt. Es ist nur ein Paar und
liefert keinen exakten Hook-Overhead. Es zeigt jedoch, dass die günstigere Richtung
des inventarisierten Mix-Vergleichs kein belastbarer Nachweis für einen generellen
PSS-Gewinn ist. Timing und Beobachtung beeinflussen das native Allokationsmuster.

### Reaktionszeiten und Wiedergabegrenzen

Die normalen UI-Beobachtungsintervalle bleiben nahe 50 ms. Ihre medianen P95-Werte
liegen je Szenario bei 51–52 ms vorher und 51–53 ms nachher. Bei 4K beträgt die
beobachtete Wartezeit in der `next`-Phase im Median 446 → 449 ms, im P95 453 → 467 ms;
im Mix 435 → 436 ms und 448 → 448 ms. Diese Werte sind durch die rund 100-ms-Abtastung
und den Hook begrenzt. Sie sind weder physische Eingabelatenz noch Display-Frametimes.
Eine reproduzierbare größere Navigationsverzögerung ist damit nicht belegt; perfekte
Gleichheit ist damit ebenso wenig nachgewiesen.

Crossfade-Dauern, Easing, Stretch, Ausgabeformat, Decoderauflösung und Decodierparallelität
wurden nicht geändert. Bereits dekodierte Quellen bleiben bei schnellen Rückwechseln
während der laufenden Blende erhalten. Nach abgeschlossener Freigabe muss ein uncached
Bild bei späterer Rückkehr erneut dekodiert werden; diese zusätzliche Bildbereitschaftszeit
ist der ausdrückliche verbleibende Tradeoff der geforderten Lease-Freigabe. Cachefähige
Quellen können weiter den bestehenden Cache verwenden. Physisches Frame-Pacing,
Optik aller Theme-/Treiberkombinationen und hörbares Audio sind nicht abschließend
abgenommen. Die virtuelle ALSA-Ausgabe ersetzt keinen Lautsprechertest.

### Optionaler Hintergrundkanal

Eine getrennte, unvollständige Diagnosevariante lässt beim deaktivierten Navigator-
Hintergrundkanal sowohl dessen LibVLC-Instanz als auch Player weg. Drei frische
Prozesse je Variante verwenden denselben ersten 1080p-Clip. PSS am Videoendpunkt:

- Regulärer Etappe-3-Stand: 471,8 / 472,8 / 471,5 MiB, Median 471,8 MiB.
- Ohne unbenutzten Hintergrundkanal: 469,7 / 469,8 / 469,0 MiB, Median 469,7 MiB.
- Beobachteter Gewinn: rund 2,1 MiB PSS und rund 2,4 MiB rechnerisch belegter glibc-Speicher;
  zwei statt drei Player. Native Callback-Puffer und aktive Videobitmap bleiben gleich.

**Nicht übernommen.** Die Variante unter `/tmp/retromind-stage3/lazy-source` ist ein
Kostenexperiment und keine sichere Implementierung für später aktivierte Hintergrund-
Themes. Der geringe Gewinn steht einem zusätzlichen kalten Initialisierungszeitpunkt
beim Themewechsel gegenüber, dessen Wiedergabegleichwertigkeit nicht belegt ist.
`ViewModels/BigModeViewModel.cs:622` und `:639` bleiben daher bei ihrer bisherigen
Initialisierung. Der AppImage-Workaround und die Player-Neuerzeugung werden ebenfalls
unverändert beibehalten.

### Native Stabilität und grafische Funktionstests

Drei unveränderte Release-Kontrollläufe mit gemischten Auflösungen erreichten jeweils
sechs Attract-Wechsel und drei Sitzungen ohne beobachteten Absturz. Sie verwenden
keinen Heaptrack und keine nativen Audio-/VLC-Diagnosegetter oder Allocatorabfragen.
Zusätzlich schließen und öffnen der unveränderte Build und der Etappe-3-Build jeweils
20 BigMode-Sitzungen mit demselben einfachen Bedienhook. Beide vollständigen Prozesse
enden mit Exitcode 0 und zuletzt 0 Playern, Medien und Callback-Puffern; je Prozess
wurden 60 erzeugte Player-Wrapper beobachtet. Die alten nativen Abstürze sind damit
in diesen Szenarien nicht reproduziert, aber **nicht als behoben nachgewiesen**.

Zwei vollständige grafische Vergleichsläufe (`functional-complete-before` und
`functional-complete-after`) enden ebenfalls mit Exitcode 0. Sie prüfen denselben
Node-Fallback für Titel 0000/0001 ohne Neustart, einen anderen Videopfad, schnelle
Rückwechsel, reale beschädigte/fehlende Video-Fikturen, erfolgreiche Wiederaufnahme,
Prism → Arcade mit echtem Hintergrundvideo → Navigator → Prism → Navigator,
Home/Zurück, drei echte Attract-Wechsel und den Schließen-/Öffnen-Versuch während des
Abbaus. Der neue Stand prüft zusätzlich die vollständige native Bitmap-Freigabe nach
dem Titel ohne Video und des unsichtbaren gestoppten Hintergrundkanals auf Home.
Die beschädigte Datei wird für spätere Attract-Zufallsziele ausschließlich im
separaten Testprofil wiederhergestellt.

Fehlkonfigurierte Zusatzläufe mit einem zu frühen Ready-Check, durch Rescan wieder
gefundenen Videodateien, ungültigem `Themes/`-Präfix vor dem themenrelativen Pfad bzw.
deaktivierter Home-Funktion werden nicht gewertet. Die korrigierten Skripte liegen
außerhalb des Repositorys. Der produktive Datenbestand wurde nicht verändert.

### Bewertung und gemeinsame Entscheidung

- **Ressourcenbesitz:** Die Bild- und Videoleases enden generationstreu; der native
  Renderer kann seine letzte Referenz bis zum Ende laufender Zeichenarbeit halten.
  Stabile belegte Anwendungsflächen sinken nachweisbar. Die Etappe-1-Tests und der
  asynchrone vollständige Abbau bestehen unverändert weiter.
- **Speicherabnahme:** Keine generelle PSS-Verbesserung. Insbesondere 4K ist in dieser
  Messreihe schlechter. Vor einer Übernahme der sofortigen Video-Bitmap-Freigabe sind
  die Wiederverwendung und Allokationsrate des Renderpfads gezielt zu bewerten.
- **Ziele 1.024/800 MiB:** In der kurzen 720p-Reihe liegen die Nachher-Spitzen und
  Desktop-Endwerte darunter. Bei 1080p liegen bereits die Spitzen bzw. Desktop-Endwerte
  darüber; 4K und der Mix verfehlen die Ziele deutlich. Eine 30–60-Minuten-Abnahme
  mit produktiven Medien, echter Audioausgabe und nativer Wayland-Sitzung ist offen.
- **Nächste Entscheidung innerhalb Etappe 3:** Bild-Freigabe und Sicherheitsguards
  getrennt von der aggressiven Video-Retirement-Strategie bewerten. Denkbar ist eine
  kurze begrenzte Wiederverwendung nach Ende der Blende, mit sofortiger Freigabe bei
  endgültigem Ausblenden/Schließen. Das wäre zuerst separat gegen die vorhandenen
  Sofortfreigabe- und Behalten-Varianten zu messen, ohne Qualitäts- oder Cacheänderungen.
  Es ist noch nicht implementiert und nicht als Einsparung verbucht.
- **Offene Risiken:** Native Abstürze ohne gesicherte Ursache; keine vollständige
  bytegenaue Zuordnung von Skia-Snapshots/GPU-/Treiberpools; spät zugestellte native
  Ereignisse ohne Medien-ID; erneut nötige uncached Dekodierung bei Rückkehr nach
  Fade-Ende. Pausierte Animationsclocks halten aus Sicherheitsgründen eine noch nicht
  vollständig weggeblendete Quelle bis zum tatsächlichen Ende oder Detach.

Es gibt keine Garbage Collections oder Trim-Aufrufe zur PSS-Korrektur, keine Änderungen
an Cachegrenzen, Decoderthreads oder globalen Allocatoreinstellungen und keine Umsetzung
der Etappen 4/5. Der Stand bleibt zur gemeinsamen Bewertung uncommittet.


### Regressionstests und Build

`dotnet build Retromind.csproj -c Release --no-restore --verbosity minimal` ist mit
0 Warnungen und 0 Fehlern erfolgreich. Der vollständige Lauf
`dotnet test tests/Retromind.Tests/Retromind.Tests.csproj --no-restore --verbosity minimal`
ist mit **2.575 bestandenen Tests**, 0 Fehlern und 0 übersprungenen Tests erfolgreich.
Das TRX liegt unter `/tmp/retromind-stage3/regressions-final.trx`.

Neu bzw. erweitert sind:

- `tests/Retromind.Tests/Helpers/CrossfadeImageTests.cs`: uncached Freigabe genau einmal,
  zwei noch nicht abgeschlossene animierte Opazitäten trotz abgelaufener Wartezeit,
  acht schnelle Richtungswechsel und Rückkehr zum alten Bitmap während der Blende,
  überholtes Clear, identische URLs, geteilte Fallbacks, fehlende und beschädigte Dateien.
- `tests/Retromind.Tests/Helpers/VideoPresentationLifetimeTests.cs`: beide Reihenfolgen
  von Stop und Fade, Rückkehr mit neuer Playbackgeneration, alte Stop-/Frame-Callbacks,
  Callback eines ersetzten Surface, 720p/1080p/4K-Ausgabe, genau einmaliger Dispose des
  Bitmapbesitzers und tatsächlich noch gültiger aufgezeichneter Avalonia-Renderzugriff.
  Weitere Tests prüfen nie angehängte Slots, Detach/Reattach, die wieder aufgenommene
  Freigabe nach Umhängen und das Entfernen der Sichtbarkeitsabonnements.
- Die bestehenden `BigModeVlcOwnershipTests`, `LibVlcVideoSurfaceTests`, Desktop-
  Sitzungs-, Theme-, Attract- und Eingabe-Tests bleiben erhalten und bestehen weiterhin.

Die UI-Unit-Tests nutzen den vorhandenen verwalteten Avalonia-Dispatcher und das echte
Skia-Backend. Lebenszyklus-Overrides werden ohne Windowing-Backend aufgerufen; echte
Theme-/Home-Wechsel werden zusätzlich im getrennten grafischen Funktionslauf geprüft.
Die Tests ersetzen keinen Treiber-/Audiotest am physischen Gerät.

Ein erster vollständiger Lauf meldete 21 GOG-Fehler, weil `/tmp` die dort ausdrücklich
geprüfte Sicherheitsreserve von 1 GiB unterschritt. Nach verlustfreier Kompression alter
Heaptrack-Textspuren und Aufräumen entbehrlicher Testbuild-Plattformdateien bestand die
komplette Suite erneut. Die neue Bild-/Video-Testgruppe war bereits zuvor erfolgreich.
Es wurden weder GOG-Produktivcode noch Speicherplatzprüfungen für diese Arbeit geändert.

### Späterer Untersuchungsansatz: physische Video-Ausgabegröße des Themes

Dieser Ansatz ist ausdrücklich **nicht implementiert**. Die späteren Ansatzpunkte
sind `Helpers/Video/LibVlcVideoSurface.cs:53` (Formatvereinbarung),
`Views/BigModeHostView.axaml.cs:706` (physischer Theme-Slot) und
`Helpers/Video/VideoSurfaceControl.cs:203` (Bitmapgröße/Kopie/Skalierung).
Drei Auflösungen sind getrennt:

- **Decoderauflösung:** Die kodierte Auflösung des H.264-/anderen Videostreams und die
  Referenzbilder von FFmpeg/VLC. Kleinere Callback-Bilder reduzieren diese Decoderbilder
  nicht automatisch.
- **VLC-Ausgabeauflösung:** Die BGRA-Pixel, die VLC in Retrominds Callback-Fläche liefert.
  Eine gezielt kleinere Ausgabe könnte Konvertierung, Callback-Fläche und nachfolgende
  WriteableBitmap verkleinern. Im aktuellen Code folgt sie weiterhin der vom Decoder
  ausgehandelten Größe einschließlich möglicher Padding-Dimensionen.
- **Avalonia-Renderauflösung:** Die tatsächlich gezeichnete Fläche aus logischen Bounds,
  Stretch/Clipping und RenderScaling. QHD-Fenstergröße allein ist kein Maß für einen
  kleinen Previewslot. HiDPI und fraktionale Skalierung müssen berücksichtigt werden.

Theoretisch sinken eng gepackte BGRA-Pixel von 31,64 MiB bei 3.840 × 2.160 auf 7,91 MiB
bei 1.920 × 1.080. Pro Callback-/Präsentationspaar wären das 47,46 MiB; während zweier
überlappender Paare höchstens 94,92 MiB. Das ist kein zusätzlicher gemessener PSS-Gewinn,
kein Beleg für eine Einsparung bei Decoderpools und keine Summe mit bereits freigegebenen
Etappe-3-Flächen. Ein künftiger Versuch benötigt einen A/B-Nachweis für Skalierungsqualität,
Frame-Pacing, erste Frames, Themewechsel, Größenänderungen und Audio. Hysterese und klare
Generationen wären nötig, damit kleine Layoutschwankungen keine ständige native
Neuformatierung auslösen. Bei UniformToFill darf die Ausgabe nicht unter die erforderliche
physische Pixeldichte des sichtbaren Ausschnitts fallen. Etappen 4 und 5 sind nicht umgesetzt.

## Etappe 3b – Ressourcenfreigabe und Bitmap-Wiederverwendung

Diese Etappe prüft die fehlgeschlagene Video-Strategie aus Etappe 3 getrennt von deren
Bild- und Sicherheitsänderungen. Die Werte aus Etappe 3 bleiben als historischer
Befund erhalten; sie sind keine Freigabe des damaligen Codes. Neue PSS-Kontrollläufe
werden getrennt von detaillierten Allokationsläufen ausgewertet.

### Getrennte Komponenten und Varianten

| Komponente | Umsetzung / Kontrolle | Entscheidender Nachweis |
|---|---|---|
| Ausgehende Bild-Leases (verworfen) | I/Q löschen die URL nach der Blende; R erhält die bisherige A/B-Lease | 7,910 MiB Pixelgewinn im Prism-Kontrolllauf, aber langsamere Rückkehr zum vorherigen Bild |
| Animations- und Generation-Guards | Effektive Opazitäten und aktuelle Fade-/Ladegeneration vor Freigabe prüfen | Zurückliegende Animationsclock, Richtungswechsel und verspätete Fortsetzungen behalten inzwischen wieder aktive Quellen |
| Video-Lebenszyklus / Callback-Sicherheit | Separater Versuch mit `VideoPlaybackLifetime`; in T verbleiben Surface-Generationen und die unveränderte Etappe-1-Barriere | R-Lifetime wird nach PSS-Nachteil verworfen; T schützt gegen alte Surface-/Detach-Aufträge |
| Nie angehängte Slots | `VideoSurfaceControl._detached` beginnt mit `true`; Subscription erst beim Attach | Gebundenes, aber nie angehängtes Control hat weder Frameabonnement noch Präsentationspixel |
| Sofortige Video-Bitmap-Freigabe | Ursprüngliche Etappe 3: inaktiven Slot nach Stop und Fade leeren | Getrennte Variante B; kein Bestandteil einer pauschalen Rücknahme der anderen vier Komponenten |

A = unveränderter `d8fa12f`; B = vollständige ursprüngliche Etappe 3; C = B ohne
Stop-/Fade-gesteuerte Bitmap-Freigabe, weiterhin mit Dispose bei Detach, Surface-
und Auflösungswechsel; D = begrenzte Wiederverwendung innerhalb der bestehenden
A/B-Slots mit Freigabe bei endgültiger Nichtbenutzung. Zusätzliche Komponentenkontrollen
G/I trennen Bildguards von Lease-Freigabe, S/N trennen Video-Sicherheitslogik von der
Unterdrückung nie angehängter Slots. Diese Kontrollen sind Diagnosevarianten und
werden nicht als zusätzliche Produktivoptionen eingeführt.

### Verworfener Kandidat D/E für die begrenzte Slot-Lebensdauer

Solange der Hauptvideokanal einen aktiven Slot hat, bleiben beide vorhandenen Slots
für die nächste Vorschau nutzbar. Jeder hält höchstens eine Bitmap; kompatible
Pixelgröße wird beim nächsten erfolgreichen Copy weiterverwendet. Der Formatvertrag
ist unverändert BGRA8888/Premul. Andere Ausgabeabmessungen ersetzen und disposen die
alte Bitmap. Es gibt weder einen globalen Pool noch eine zusätzliche Haltefrist.

Bei `ActiveIndex=-1` endet die Wiederverwendung nach der vorhandenen, tatsächlich
abgeschlossenen Blende und bestätigter Input-Ruhe. Ein versteckter, gestoppter
Teilbaum gibt ebenfalls frei. Beim Detach oder Surface-Tausch endet der UI-Besitz
wie bisher. Renderkommandos können ihre bereits aufgezeichnete Plattformreferenz
bis zum Ende laufender Renderarbeit behalten; der Regressionstest aus Etappe 3
prüft diesen echten Avalonia-Zeichenpfad weiterhin.

Für die Wiederverwendung muss gespeicherter Pixelinhalt von wiederverwendbarem
Speicher unterschieden werden. Eine neue Playbackgeneration invalidiert den alten
Zeicheninhalt. Erst eine erfolgreiche Framekopie markiert die Bitmap für diese
Generation als zeichnungsfähig. Auch nach einem Größenwechsel mit zunächst
fehlgeschlagener Kopie werden die neuen, noch nicht gefüllten Pixel nicht als Frame
veröffentlicht. Die native VLC-Format-/Lock-/Copy-/Free-Implementierung bleibt
unverändert; temporäre Diagnosezähler gehören nur in die Messbuilds.

### Messverfahren und dauerhafte Reproduzierbarkeit

Die neue Suite unter `benchmarks/video-slot-lifetime/` enthält Quellen der beiden
externen Startup-Hooks, Runner, isolierten Variantenbauer, Fixture-Generator,
Auswertung, genaue Patchstände B/D und kompakte Ergebnisse. Der Produktivbuild
schließt `benchmarks/**/*.cs` bereits aus. Die neuen Skripte schreiben keine Daten
in produktive Profile und benötigen keinen Git-Checkout-Wechsel.

Verwendet werden dieselben historischen Bilddateien, H.264-Dateien und dieselbe
Auswahlfolge wie in Etappe 3. Je Prozess frisches HOME/XDG und kopierte JSON-Dateien;
Medien und Themes werden schreibgeschützt genutzt und vor einer lokalen Ersetzung
vom privaten Hardlink getrennt. Die Hashes der Eingangsmedien, Builds, Werkzeuge und
der Laufreihenfolge werden mit den kompakten Ergebnissen gespeichert. Neu erzeugte
Video-Fixtures mit anderer FFmpeg-/Encoder-Version gelten ausdrücklich als neue
Baseline. Der Generator ersetzt daher nicht die Aussage „identische historische
Medien“ für die hier durchgeführten Vergleiche.

Virtuelle QHD-Ausgabe 2.560 × 1.440, Skalierung 1, KWin/Xwayland, unverändertes
AppImage-LibVLC und AppImage-Audio-Workaround. Frische Prozesse in wechselnder
Buildreihenfolge; 13 Titel, 1,2 Sekunden Verweilen nach Bereitschaft, zwei Sekunden
ruhige Vorschau, schneller Close/Open-Versuch während des Abbaus, zwei weitere
Sitzungen. PSS wird etwa alle 100 ms extern aus `/proc` gelesen. Zusätzlich zu den
bisherigen Punkten gibt es 20 Sekunden weitere Desktop-Ruhe nach der letzten
Drei-Sekunden-Messung.

Der kleine Kontrollhook enumeriert bei Videoläufen keine Bild-/Bitmapobjekte und
ruft weder `malloc_info` noch native Player-Diagnosegetter auf. Er benötigt weiterhin
verwaltete Statusabfragen und einen etwa 50-ms-UI-Callback zum Steuern der Folge;
das ist keine vollständig unbeeinflusste Alltagsnutzung. Separat instrumentierte
Builds zählen echte Bitmap-Neuanlagen, kumulierte Pixelbytes und die erste
Wiederverwendung pro erfolgreicher VLC-Formatgeneration. Ein begrenzter Ring misst
UI-Kopierintervalle. Nur dort werden Bild-Leases, Plattform-Bitmaps und glibc-
Statistiken erfasst. Beide Messklassen werden nicht zu einem PSS-Mittelwert vermischt.

Die glibc-Statistik wird ohne Doppelzählung aus den globalen `system current`- und
`total fast/rest/mmap`-Werten abgeleitet. Arena minus freie Blöcke plus glibc-mmap ist
nur eine Näherung aktiver Allokationen. Die Linux-Pixel des Avalonia-`UnmanagedBlob`
verwenden direkte mmap/munmap-Zuordnungen außerhalb dieser Statistik. CPU-Bitmaps,
VLC-Callback-Puffer und unbekannte GPU-/Snapshotressourcen werden deshalb nicht
pauschal mit glibc summiert. Physische Präsentationszeiten, native Wayland-Ausgabe
und menschlich hörbares Audio bleiben gesonderte Abnahmepunkte.

### Zusätzlich isolierte Variante E

D invalidiert den aufgezeichneten Bildinhalt bei jedem Wiedergabebeginn, auch wenn
der Zielslot noch Opazität 0 hat. E behält dieselbe Slot-Lebensdauer und dieselben
Playback-/Surface-Guards, invalidiert bei Begin aber nur bereits sichtbaren Inhalt.
Ein unsichtbarer A/B-Slot wird ausdrücklich beim Aktivieren invalidiert. Der Render-
Guard verhindert weiter, dass Pixel einer älteren Playbackgeneration gezeichnet
werden. Diese zwei Zeilen ändern weder Bitmap-Konstruktion noch native Decoder-
oder Callback-Puffer. Sie vermeiden eine unnötige Renderaufzeichnung vor der ersten
verwendbaren Präsentation; daraus folgt noch kein nachgewiesener PSS-Ursachenzusammenhang.

E erhält einen eigenen abwechselnden A/E-Block mit erneut frischen Prozessen.
Die ursprüngliche A/B/C/D-Serie und dieser spätere Block werden nicht gepoolt.
Der kleine Kontrollhook wurde für Bildläufe zusätzlich gegen eine leere Visual-
Aufzählung abgesichert; der ausgeführte Videopfad bleibt unverändert. Der alte
Hookstand ist als kleiner Rückwärtspatch reproduzierbar, beide Hashes sind erfasst.

Zwei Kontrollprozesse der ersten Serie überlappten versehentlich mit einer
Buildvorbereitung. Beide wurden vollständig ausgeschlossen und mit frischen
Prozessen wiederholt. Ein ebenfalls überlappender B-Diagnoseprozess wurde separat
wiederholt. Die Auswertung liest die Ausschlussmarker und übernimmt keine einzelnen
„guten“ Phasen aus diesen Prozessen. Weitere Build-/Testpausen verwenden einen vom
Runner vor dem App-Start gesetzten Wartepunkt statt eines älteren Fertigmarkers.

### Bereits abgeschlossene Sicherheits- und Komponentenprüfungen

Die vollständige Suite auf D besteht mit **2.579 bestandenen Tests, 0 Fehlern und
0 übersprungenen Tests**. Zusätzlich zu Etappe 3 prüfen die Tests die wiederholte
kompatible A/B-Wiederverwendung, eine neue Playbackgeneration ohne passende Pixel,
fehlgeschlagene erste Kopie nach Größenwechsel und das Entfernen beider Lifetime-
Eventabonnements beim Surface-Tausch/Detach. Aufgezeichnete Renderreferenzen bleiben
beim einmaligen Dispose des UI-Besitzes gültig. Etappe-1-Regressionen sind unverändert.
Die finale Validierung bezieht sich auf T; Ergebnisse und Einschränkungen stehen unten.

Die getrennten G/I-Prism-Läufe halten bei gleichem Cachebudget 4 bzw. 3 uncached
Leases: **19,915 → 12,005 MiB**. Einzigartige Bildpixel im UI gehen von 48,232 auf
40,322 MiB zurück; der Cache bleibt in beiden Läufen bei 28,317 MiB. Das sind
**7,910 MiB weniger aktiver Pixelbesitz**, unabhängig von einem glibc-/PSS-Effekt.
Vorwärts-Bereitschaft P95 liegt im einzelnen Diagnosepaar bei 632/635 ms mit
100-ms-Abtastung. Ein nach vollständig abgeschlossener Blende zurückgeholtes
uncached Bild muss neu dekodiert werden; dafür wurde der unten ausgewertete gesonderte Rücksprunglauf durchgeführt.

Die S/N-Kontrollen halten im normalen Navigator-Lauf beide zwei Videobitmaps und
verwenden elfmal wieder. Der dort nicht bespielte Hintergrundslot liefert keine
Frames, daher lässt sich in genau diesem Szenario kein separater PSS-Gewinn der
Never-Attached-Sperre zeigen. Der Regressionstest stellt deren tatsächliche
Wirkung sicher: kein Abonnement und keine Pixel vor dem ersten Attach, normale
Kopie nach Attach, keine Allokation aus eingefangenen Callbacks nach Detach.

Die funktionalen Läufe A und D enden beide mit Exit 0 und ohne Hookfehler. Nach
defektem und anschließend fehlendem Video hält A noch zwei Plattform-Bitmaps mit
zusammen **39,888 MiB**; D hat dann null UI-Bitmaps und null beobachtete lebende
Plattformobjekte. In diesem Lauf waren die schwachen Referenzen bereits gesammelt;
er allein unterscheidet deshalb nicht zwischen Dispose und Finalisierung. Der
einmalige explizite Dispose ist separat durch den Renderreferenz-Regressionstest
abgesichert. Ohne erzwungene GC endet der BigMode-Abbau in beiden Builds mit null
Playern, Medienreferenzen, Retirement-Einträgen und Callback-Puffern.

Gemeinsamer Node-Fallback ohne Neustart, unterschiedliche Dateien, schnelle
Richtungswechsel, defekte/fehlende Dateien und Wiederaufnahme, fünf Themewechsel,
Arcade-Hintergrundvideo mit Home/Rückkehr, drei AttractMode-Schritte und schneller
Close/Open-Versuch laufen durch. Bei Home hält A eine Hintergrund-Bitmap, D null.
Beide Läufe beobachten insgesamt 13 unterschiedliche Player; der AppImage-
Audio-Workaround wird weiter ausgeführt. Dieser Gleichstand ist kein Hörtest.

### Rückfallstand F und Snapshot-Kontrolle Q

Auch D und E erfüllen die Abnahme im ersten bzw. ihrem eigenen Folgeblock nicht
überzeugend: D liegt nach Desktop-Ruhe im Median rund 267 MiB über seinem A,
E am Auswahlende rund 289 MiB und nach Desktop-Ruhe rund 203 MiB über seinem A.
Die Reihen streuen erheblich; eine genaue Ursache dieser Differenzen ist damit
nicht bewiesen. Eine Freigabe allein wegen niedrigerer Bitmap-Allokationsrate wäre
aber nicht gerechtfertigt. Die alternative Freigabe bei endgültiger Nichtbenutzung
bleibt deshalb ein verworfener Messkandidat.

F entfernt diese Freigabestrategien vollständig: keine Video-Fade-End-Timer,
keine Sichtbarkeitsabonnements und keine Playback-Start-Invalidierungen. Wie in A
halten die bestehenden Controls kompatible Bitmap-Speicher bis Größenwechsel,
Surface-Tausch oder Detach. Playback-/Surface-Guards, Never-Attached-Sperre und
Render-Epoch-Prüfung bleiben. Auch F zeigt im eigenen A/F-Block eine ungünstige
4K-PSS-Verteilung; er wird deshalb ebenfalls nicht allein aufgrund der gelungenen
Ressourcensicherheit freigegeben.

Ein weiterer kontrollierter A/G/I/N-Block trennt erstmals auch die *wenig
instrumentierten* Videoläufe nach Bildguards, Bildfreigabe und alter Video-
Sicherheitsimplementierung. Die n=2-Punktwerte reichen nicht für eine verlässliche
Kausalzuordnung; besonders N fällt mit hoher PSS auf. Deshalb folgte ein eigener
A/I/Q-Block mit vier frischen Prozessen je Stand. Q entspricht F bis auf die
Veröffentlichung des Präsentationszustands: Generation/Stopped werden gemeinsam
als unveränderliches Objekt publiziert, Frame-Leser greifen mit `Volatile.Read`
darauf zu. Der Besitzer serialisiert weiterhin Begin/ConfirmStopped. Pro
Wiedergabeübergang entsteht ein kleines verwaltetes Zustandsobjekt, niemals pro
Frame. Die Frame- und Render-Leser nehmen keine zusätzliche Übergabesperre.

Der zusätzliche Regressionstest hält die private Übergabesperre und lässt einen
zweiten Thread den bereits publizierten Zustand lesen. Q liefert ihn ohne Warten;
veraltete Stopbestätigungen und einmalige Stopmeldungen bleiben abgesichert.
Die vollständige Suite auf Q besteht mit **2.580/2.580 Tests**. Q ist zu diesem
Zeitpunkt ein Kandidat, kein Nachweis, dass Monitor-Synchronisation die PSS-Ursache
war. Der alternative Veröffentlichungsweg ändert keine native Allokation und
keinen VLC-Callback-Pufferpfad.

Der für die Vergleiche verwendete A-Build wurde unabhängig neu aus `d8fa12f`
erzeugt. Ein Offline-Metadatenvergleich bestätigt **444 identische normalisierte
IL-Methoden** in `VideoSurfaceControl`, `LibVlcVideoSurface` und `BigModeViewModel`.
Der Audit lädt weder die Anwendung noch native Bibliotheken. Unterschiedliche
Assembly-Hashes durch Buildpfad/Metadaten sind separat gespeichert.

Nach der ersten Messgruppe wurde die identische synthetische Fixture auf das
ignorierte SSD-Arbeitsverzeichnis kopiert, weil das historische `/tmp`-tmpfs nur
noch wenig freien Platz hatte. Beide Builds eines neuen Blocks verwenden dieselbe
Kopie; Medien-SHA-256 wurden geprüft. Die Blöcke werden getrennt ausgewertet.
Der Fixture-Generator wurde mit archivierten Clips ausgeführt; **alle 128 erzeugten
PNG-/JPEG-Dateien sind byteidentisch** zu den historischen Eingangsdateien. Die
neu erzeugten Modell-IDs/normalisierten JSON-Dateien ersetzen die historischen
Eingangsprofile ausdrücklich nicht in der laufenden Vergleichsserie.

### Abgeschlossener A/I/Q-4K-Block

Vier frische Prozesse je Stand, gleiche Eingänge und wechselnde Reihenfolge.
MiB, Median mit Min–Max; diese Gruppe wird nicht mit früheren A-Läufen gepoolt:

| Stand | Auswahlende | Übergangspeak | Desktop nach zusätzlicher Ruhe | Weiter bis erster Frame P95 |
|---|---:|---:|---:|---:|
| A | 2.203,6 (2.018,9–2.951,0) | 2.875,1 (2.456,1–3.044,0) | 1.928,9 (1.825,7–2.121,9) | 454,5 ms |
| I – nur Bildänderung | 2.040,6 (2.010,7–2.502,7) | 2.652,7 (2.475,2–2.894,1) | 1.408,1 (1.177,1–2.101,7) | 451,0 ms |
| Q – bereinigte Wiederverwendung und Snapshot-Guards | 2.114,4 (1.904,0–2.559,5) | 2.714,8 (2.339,8–2.993,5) | 1.652,2 (1.411,6–2.462,9) | 451,5 ms |

Q zeigt hier keine reproduzierte erhebliche Verschlechterung gegenüber A und
keinen zusätzlichen Bitmap-Allokationsbedarf. Ein einzelner Q-Desktoplauf liegt
mit 2.462,9 MiB dennoch deutlich hoch. Weder die günstigeren Mediane noch diese
Einzelspitze beweisen eine allgemeine Wirkung des Monitor-/Snapshot-Wechsels.
Die Baseline selbst reicht über alle getrennten Blöcke in breite PSS-Bereiche.
Eine enge Nichtunterlegenheitsgrenze oder ein 30–60-Minuten-Langzeitnachweis ist
damit nicht erbracht. Die absoluten Ziele 1.024/800 MiB werden weiterhin verfehlt.

Der Q-Funktionslauf endet mit Exit 0 ohne Hookfehler. Die ursprüngliche Retention
ist sichtbar: bei fehlender Vorschau noch zwei UI-/Plattform-Bitmaps, zusammen
39,888 MiB, nach vollständigem Abbau null. Damit ist der native Gewinn der frühen
D-Freigabe ausdrücklich **kein** Vorteil des aktuellen Rückfallkandidaten.

Zusätzlich besteht ein Q-Kontrolllauf mit sechs AttractMode-Schritten und drei
BigMode-Sitzungen ohne Profiler, ohne Allokationsinstrumentierung und ohne
`malloc_info`/native Player-Diagnosegetter. Er beobachtet 15 unterschiedliche
Player durch den erhaltenen AppImage-Workaround und endet mit null Playern,
Medienreferenzen, Retirement-Einträgen und Callback-Puffern. Der ältere native
SIGSEGV-Befund ist damit nicht reproduziert, aber weiterhin nicht als behoben
nachgewiesen. Die verwaltete 50-ms-Steuerung bleibt auch in diesem Kontrolllauf
als möglicher Timing-Einfluss vorhanden.

Die Aussage zu direkten mmap/munmap-Zuordnungen betrifft ausdrücklich den
`UnmanagedBlob`-Pfad der verwendeten WriteableBitmap-Implementierung. Gewöhnliche
Skia-Bilddekodierungen, Bitmap-Snapshots und Zwischenkopien können dagegen über
glibc allokieren. Bildpixel dürfen daher weder pauschal aus den Arena-Werten
herausgerechnet noch zusätzlich zu ihnen vollständig addiert werden. Eine
zuverlässige PSS-Aufteilung aller Skia-/GPU-Kopien ist mit diesen Zählern nicht möglich.


### Verwerfung der Bildfreigabe und Abschlusskandidat R

Der ergänzte Prism-Rücksprungtest wartet nach jedem Wechsel 1,2 Sekunden und damit
länger als die 700-ms-Blende. Drei Wechsel 12→11→12 unterscheiden einen schnellen
Richtungswechsel während der Blende von der Rückkehr nach tatsächlich freigegebenen
Pixeln. A benötigt bis zur Bildbereitschaft rund **420–422 ms**. I benötigt für
Rückwärtswechsel **632–635 ms**, Q **528–634 ms**; die Vorwärtsrückkehr liegt ähnlich.
Die Steuerung tastet in etwa 100-ms-Schritten ab. Die Größenordnung einer zusätzlichen
Dekodierung ist dennoch klar sichtbar. Es handelt sich um verspätete Artwork-
Bereitschaft, nicht um eine belegte Blockierung des UI-Threads.

Ursache: A kann im internen, begrenzten zweiten Image direkt das bereits geladene
Bild verwenden. Nach `SetUrl(inactive, null)` existiert insbesondere bei
`DisableCache=True` diese Bitmap nicht mehr; die Rückkehr durchläuft erneut den
bestehenden seriellen Dekodierpfad. Ein kleinerer aktiver Pixelbesitz allein erfüllt
damit die Navigationsabnahme nicht. Es werden weder Preloads ergänzt noch
Cache-/Dekodierlimits zur Kompensation angehoben. Der Lease-Freigabeanteil aus I/Q
wird gezielt entfernt. Klare URL, Detach und tatsächlicher Slot-Ersatz geben die
Leases weiterhin über den unveränderten Helper frei.

R kombiniert Qs Video-Guards mit dieser ursprünglichen Bildlebensdauer. Die
Animationsguards prüfen weiterhin effektive Opazitäten und aktuelle Generationen
vor Hide/Clear. Fallbacks werden erst nach ihrer tatsächlich abgeschlossenen Blende
versteckt. Es gibt keine neue native Puffermanipulation, kein zusätzliches Pooling
und keine Haltefrist. Die Freigabestrategie D/E bei Home oder dauerhaft fehlendem
Video wird ebenfalls nicht übernommen; die kleinen vorhandenen Slotmengen bleiben
bis Detach/Surface-/Größenwechsel erhalten. Dies ist der ausdrücklich zugelassene
Rückfall auf die ursprüngliche Video-Bitmap-Wiederverwendung.

Die zusätzliche 1080p-Bestätigung A–Q–Q–A reproduziert den zunächst rund 183 MiB
höheren Q-Endwert nicht konsistent: Auswahlende A 967,7/1.061,2 MiB gegenüber Q
896,0/999,3 MiB. Nach Desktop-Ruhe A 904,0/864,5 gegenüber Q 825,0/941,4 MiB.
Die ursprünglichen zwei Matrixläufe bleiben unverändert in den Daten erhalten;
positive Folgewerte ersetzen sie nicht. Das zeigt erhebliche Prozessstreuung,
keinen Beweis gleicher PSS und keine gesicherte Kausalzuordnung.

Die gezielten Tests des R-Stands bestehen mit **29/29**, die vollständige Suite mit
**2.582/2.582**, jeweils ohne Fehler/Skip. Der neue Rückkehrtest sperrt nach zwei
vollständig geladenen und ausgeblendeten Bildern den seriellen Dekodierungsplatz;
auch uncached muss das vorherige Bild trotzdem aus exakt derselben Bitmap wieder
bereit werden. Weitere Tests prüfen verzögerte Animationsclocks, stale Clear,
gemeinsame Fallback-Leases, fehlende/beschädigte Dateien, Größenwechsel,
Never-Attached/Detach/Reattach, alte Surface-/Playback-Callbacks, fehlgeschlagene
erste Framekopie, kompatible A/B-Wiederverwendung, nicht blockierende Snapshot-
Leser sowie die exakt einmalige Freigabe bei noch aufgezeichneter Renderreferenz.

Fundstellen der verworfenen Variante R (archivierter Build, nicht aktueller Produktivstand):

- `Extensions/CrossfadeImage.cs:390`, `:468`, `:648`: tatsächliches Fade-Ende und
  Generation vor Clear/Hide prüfen; vorhandene A/B-Leases bis Ersatz/Clear behalten.
- `Helpers/Video/VideoSurfaceControl.cs:57`, `:83`, `:99`, `:122`: Abonnements,
  Never-Attached-Sperre und Filter für veraltete Frame-Aufträge.
- `Helpers/Video/VideoSurfaceControl.cs:147`, `:220`, `:238`: kompatible Speicher-
  Wiederverwendung, UI-Dispose und Render-Epoch-Guard.
- `Helpers/Video/VideoPlaybackLifetime.cs:8`: konsistente, unveränderlich publizierte
  Generation/Stop-Bestätigung ohne Sperre im Frame-/Render-Lesepfad.
- `ViewModels/BigModeViewModel.Preview.cs:710`, `:1064` und
  `ViewModels/BigModeViewModel.cs:825`, `:848`: Bestätigung nach vorhandener nativer
  Freigabe und Beginn einer neuen Präsentationsgeneration. Die Etappe-1-Cleanup-
  Implementierung und der AppImage-Workaround bleiben unverändert.


### R verfehlt die Abnahme; getrennte Sicherheitsvariante T

Der abschließende A–R–R–A / R–A–A–R-Block besteht aus vier frischen Prozessen je
Stand. Alle Werte einschließlich des günstigsten R-Laufs bleiben enthalten:

| Stand | Auswahlende PSS, Median (Min–Max), MiB | Übergangspeak, MiB | Desktop-Ruhe, MiB |
|---|---:|---:|---:|
| A | 1.584,3 (1.553,3–1.624,9) | 2.477,6 (2.406,8–2.496,1) | 1.349,5 (1.151,1–1.577,4) |
| R | 2.336,4 (1.934,4–2.453,3) | 2.883,6 (2.494,4–2.976,9) | 1.944,1 (1.197,5–2.081,2) |

R liegt damit am Ende rund 752 MiB / 47,5 % höher. Der günstigere Q-Median eines
früheren Blocks genügt nicht zur Freigabe. Auch ohne die Bildfreigabe ist dieser
Video-Sicherheitsverbund nicht abgenommen. Die genaue native Ursache ist mit
PSS, exakten Präsentationszählern und punktuellen Arena-Snapshots nicht bewiesen;
aus dem Ergebnis folgt insbesondere nicht, dass kleine Zustandsobjekte selbst
Hunderte MiB belegen. Es rechtfertigt keine pauschalen Allocator-Eingriffe.

T entfernt deshalb gezielt die zusätzlich gekoppelte Playback-Lifetime und ihre
Frame-/Render-Abfragen. `LibVlcVideoSurface`, `CrossfadeVideoSurfaceControl` und
beide betroffenen ViewModel-Dateien entsprechen wieder bytegenau `d8fa12f`.
Das betrifft ausschließlich den nicht abgenommenen Etappe-3-Zusatz; die native
Referenzverwaltung, Stop-/Detach-Fehlerbehandlung, Stop-Barrieren und der vollständig
beobachtbare asynchrone Abbau aus Etappe 1 werden nicht verändert.

Separat erhalten bleiben die Guards vor verspätetem Image-Hide/Clear, die erst beim
Attach aktivierte Frame-Subscription sowie Surface-Generationen vor dem Einreihen
und Ausführen eines UI-Copy-Auftrags. Ein neues boolesches Präsentationsflag
verhindert, dass nach einer fehlgeschlagenen ersten Kopie ungefüllte Bitmap-Pixel
gezeichnet werden. Es wird nur im UI-Pfad gesetzt, allokiert nichts und benötigt
keine Sperre. Die vorhandene VLC-Eigentümer-Barriere und First-Frame-Umschaltung
bleiben für Wiedergabewechsel maßgeblich; T führt kein zweites Playback-Protokoll ein.
Mangels Bildfreigabe ist auch der neue Fallback-End-Timer aus R nicht erforderlich
und entfällt; das ursprüngliche Fallback-Fade bleibt erhalten.

`T.patch` hält diesen isolierten Stand reproduzierbar fest. T wurde in einem eigenen
A/T-Block geprüft; R- und Q-Werte werden dafür weder ersetzt noch gepoolt.


T besteht **27 gezielte und 2.580 vollständige Regressionstests**, jeweils ohne
Fehler/Skip. Die verbliebenen Video-Tests prüfen Surface-/Detach-Generationen,
kompatible A/B-Wiederverwendung, Größenwechsel, ungefüllte Pixel und echte
Renderreferenzen. Die Generationstests des verworfenen zusätzlichen Playback-
Protokolls werden nicht als Prüfung des T-Produkts ausgegeben. Bestehende VLC-
Eigentums-, Cleanup- und Wiederaufnahme-Regressionen bleiben unverändert erhalten.
Der Release-Build besteht mit null Warnungen und null Fehlern.

Die unabhängige Baseline-Prüfung wurde auf **785 normalisierte IL-Methoden** erweitert,
einschließlich `AsyncImageHelper`, beider Crossfade-Controls und der zugehörigen
compilererzeugten verschachtelten Methoden. Es gibt weiterhin keine Unterschiede
zwischen dem verwendeten historischen A und einem frischen d8fa12f-Build.
Die Runtimekonfiguration und das Dependency-Manifest sind ebenfalls identisch.


### A/T-4K-Abschlussvergleich

Vier Prozesse je Build, Reihenfolge A–T–T–A / T–A–A–T, frische identische Profile,
keine gleichzeitigen Builds/Tests. Die 100-ms-PSS-Stichproben ergeben:

| Messwert | A, Median, MiB | T, Median, MiB |
|---|---:|---:|
| Auswahlende | 2.015,7 | 1.678,4 |
| Übergangspeak | 2.490,1 | 2.467,2 |
| Desktop nach erster Rückkehr / Abbau + 3 s | 1.376,6 | 1.166,2 |
| Nach allen drei BigMode-Sitzungen + 3 s | 1.430,5 | 1.245,0 |
| Nach zusätzlichen 20 s Desktop-Ruhe | 1.433,2 | 1.245,0 |

Der komplette Bereich am Auswahlende ist A 1.563,1–2.210,7 und T
1.587,6–1.735,2 MiB. Nach Ruhe reicht A von 1.145,5 bis 2.051,5, T von
1.118,0 bis 1.274,2 MiB. T zeigt hier keine reproduzierte erhebliche PSS-
Verschlechterung. Die günstigeren Mediane sind kein Beweis eines allgemeinen
337-MiB-Einsparmechanismus: aktive Video-Bitmapmengen werden gerade nicht verkleinert,
und die über verschiedene Messblöcke erhebliche A-Streuung bleibt bestehen.

Die CPU während der ersten BigMode-Sitzung beträgt A/T **125,1/124,9 %**
(100 % = ein CPU-Kern), UI-Steuerungsintervalle P95 beide **52 ms**,
Weiterwahl bis erste passende Frame-Revision P95 **448,0/447,5 ms**. Diese
50-ms-Steuerungs- und 100-ms-Beobachtungswerte ersetzen keine Compositor-Frametimes.
Alle beobachteten Player/zugewiesenen Medien, Retirement-Einträge und Callback-
Puffer sind nach dem vollständigen Abbau und nach Ruhe null.


### Weitere A/T-Medienmatrix und Prism-Rückkehr

MiB, Median; getrennte frische Prozesse. 720p/Mix/Prism je ein Prozess pro Build,
1080p je zwei. Diese kleinen Paare dienen der Szenarienprüfung und sind keine
statistisch enge Nichtunterlegenheits- oder Langzeitabnahme.

| Szenario | Build | n | Auswahlende | Übergangspeak | Desktop-Ruhe | Weiterwahl P95 |
|---|---|---:|---:|---:|---:|---:|
| 720 | A | 1 | 735.4 | 794.6 | 755.2 | 436.0 ms |
| 720 | T | 1 | 759.3 | 809.5 | 758.1 | 433.0 ms |
| 1080 | A | 2 | 968.6 | 1104.7 | 949.1 | 440.0 ms |
| 1080 | T | 2 | 1012.9 | 1207.0 | 937.2 | 440.5 ms |
| mixed | A | 1 | 1175.2 | 1634.0 | 1253.6 | 446.0 ms |
| mixed | T | 1 | 985.3 | 1540.7 | 1105.6 | 444.0 ms |
| images-bounce | A | 1 | 492.3 | 499.0 | 513.9 | 635.0 ms |
| images-bounce | T | 1 | 500.5 | 500.5 | 518.8 | 634.0 ms |

Beim 1080p-Paar liegt T am Auswahlende im Median 44,2 MiB höher und nach Ruhe
11,8 MiB niedriger. Sein Übergangspeak liegt 102,3 MiB höher. Diese Abweichung wird
nicht durch günstigere 4K-Werte verrechnet; zwei Wiederholungen reichen nicht für
eine sichere allgemeine Zuordnung. Bei 720p und Prism liegen die Unterschiede im
kleinen zweistelligen bzw. einstelligen MiB-Bereich.

Die drei nach abgeschlossener 700-ms-Blende ausgelösten Prism-Rücksprünge benötigen
A 421/421/423 ms und T 423/422/421 ms; die Vorwärtsrückkehr A 421/422/421 und
T 422/422/422 ms. Die gemessene Verzögerung der I/Q-Bildfreigabe ist damit im
tatsächlich verbleibenden T-Code nicht vorhanden.

Die UI-Beobachter-P95 bezieht sich auf protokollierte **geänderte Zustände**;
identische Folge-Snapshots werden zusammengefasst. Sie ist kein Perzentil aller
UI-Callbacks und keine Compositor-Frametime. Der kompakte Export enthält deshalb
auch die größten beobachteten Callback-Abstände. Für Wiedergabepacing dienen
zusätzlich die separat erfassten UI-Kopierintervalle; physische Präsentation bleibt
unverifiziert.


CPU-Mediane A/T liegen bei 720p bei 42,6/42,6 %, bei 1080p 56,4/56,6 %, im Mix
73,4/73,4 % und bei Prism 53,7/53,9 %. Die größten beobachteten UI-Callbackabstände
im 4K-Block haben Prozessmediane von 467,5/473,5 ms. Diese seltenen Abstände
enthalten auch BigMode-Aufbau und Übergänge; sie sind kein reiner Steady-State-
Framewert. Sie treten in ähnlicher Größenordnung bereits in A auf und werden
nicht durch das günstige P95 verschwiegen.


### Tatsächlich verbleibende Pixelressourcen und funktionale Sicherheit von T

Die instrumentierten T-Läufe bestätigen an derselben stabilen A/B-Instanz nach den
13 Titeln die ursprüngliche Allokationsrate:

| Eingänge | Neuanlagen | Wiederverwendungen | Kumulierte angelegte Pixel, MiB | Noch gehaltene Video-Pixel, MiB | UI-Kopierintervall P95 |
|---|---:|---:|---:|---:|---:|
| 720p | 2 | 11 | 7,207 | 7,207 | 42,019 ms |
| 1080p | 2 | 11 | 15,967 | 15,967 | 42,139 ms |
| 4K | 2 | 11 | 63,809 | 63,809 | 43,065 ms |
| Mix | 13 | 0 | 177,568 | 35,508 | 42,585 ms |
| Prism Bilder | 0 | 0 | 0 | 0 | entfällt |

Die A-Werte sind für Anzahl/Pixel gleich. Die Vergleichswerte der A-Kopierintervalle
liegen bei 41,828 / 42,184 / 42,608 / 42,594 ms. Eine relevante Pacing-
Verschlechterung ist in diesen Kopiersamples nicht erkennbar; tatsächliche
Displaypräsentation wird damit nicht vermessen. Die native Ausgabe bleibt im
vorhandenen festen Format BGRA8888/Premul. Inkompatibilität bedeutet in diesem
Vertrag andere Pixelabmessungen; ein beliebiger zusätzlicher Formatschlüssel ist
nicht erforderlich. Größenwechsel disposen weiterhin die alte Bitmap sicher.

Gegenüber B sinkt bei konstantem 4K die Anzahl von 13 auf 2 und die kumulierte
Pixelneuanlage von 414,756 auf 63,809 MiB: **350,947 MiB vermiedener Allokationstraffic**
innerhalb dieser Folge. Dafür bleiben bei T im ruhigen Videokanal wie bei A zwei
statt einer Bitmap, also 31,904 MiB mehr als bei B. Der notwendige Crossfade-Peak
beträgt in beiden Strategien zwei. Es gibt keine zusätzliche globale Poolmenge.
Bei inkompatiblen Auflösungen wird nicht versucht, mehr als das bestehende A/B-Paar
auf Vorrat zu halten.

Prism hält bei T wie A 138 Bild-Leases mit 48,232 MiB einzigartigen UI-Pixeln,
darunter vier uncached Leases mit 19,915 MiB; der Cache umfasst 28,317 MiB. Die
7,910-MiB-Bildoptimierung aus I wird zugunsten der nachgewiesenen schnellen Rückkehr
nicht übernommen. Nach dem Prism-Abbau bleibt unter anderem das sichtbare
uncached Desktop-Wallpaper aus `Views/MainWindow.axaml:62` erhalten. Bild-Leases
im Desktop müssen daher nicht null sein.

**Zählergrenze:** Die Diagnose hält Controls nur schwach. Die oben angegebene
kumulierte Allokation gilt für die erste stabile 13-Titel-Folge. Nach Sammlung
alter Controls ist die Summe ihrer noch auslesbaren Felder kein vollständiger
Prozess-Lebenszeitzähler. Spätere Funktions-/Abbaupunkte werden für aktiven Besitz,
nicht für kumulierten Traffic aller bereits gesammelten Controls ausgewertet.
Auch ein bereits gesammeltes Plattformobjekt beweist allein keine explizite Freigabe;
der echte Renderreferenz-Test prüft deshalb zusätzlich Dispose genau einmal und
einen noch gültigen aufgezeichneten nativen Renderzugriff.

Der T-Funktionslauf endet mit Exit 0 und ohne Hookfehler. Gemeinsamer Node-Fallback
und identischer Pfad starten kein unnötiges neues Video; verschiedene Dateien,
rasche Richtungswechsel, kaputte/fehlende Dateien und Wiederaufnahme, fünf Themes,
Arcade-Hintergrund mit Home/Rückkehr, Attract und schneller Close/Open werden
abgedeckt. Bei fehlendem Video bleiben wie in A höchstens die zwei vorhandenen
Slot-Bitmaps (hier zusammen 39,888 MiB), bei Home eine Hintergrund-Bitmap.
Nach vollständigem Abbau sind beobachtete Video-Bitmaps/Plattformobjekte,
Player, Medienreferenzen, Retirement-Einträge und Callback-Puffer null.

Der separate T-Stabilitätslauf mit sechs AttractMode-Schritten und drei BigMode-
Sitzungen besteht ohne Profiler, Allokationszähler oder zusätzliche native
Diagnosegetter; 15 verschiedene Player werden beobachtet und am Ende null aktive
native Besitzer/Puffer. Der Funktionslauf beobachtet wie A insgesamt 13 Player.
Die produktive Player-Neuerzeugung durch den AppImage-Audio-Workaround ist weder
entfernt noch umgangen. ALSA-Null bestätigt keinen hörbaren Audiotest. Der frühere
native SIGSEGV ist in diesen T-Läufen nicht reproduziert; er bleibt ausdrücklich
ein offener Stabilitätsbefund und wird nicht als behoben geschlossen.


### Freier Allocatorspeicher gegenüber aktiven Ressourcen

Die zusätzlichen instrumentierten 4K-Prozesse R/T besitzen beide zwei Bitmaps,
legen 63,809 MiB an und verwenden elfmal wieder. Trotzdem unterscheiden sich ihre
Allocator-Höchststände deutlich. Die folgenden Werte stammen ausdrücklich aus
**separaten Diagnoseläufen**, nicht aus den wenig instrumentierten PSS-Abnahmeläufen:

| Zustand | Stand | glibc-Arenen | Arena aktuell, MiB | Freie Blöcke | Frei, MiB | Busy ungefähr, MiB |
|---|---|---:|---:|---:|---:|---:|
| Auswahlende | R | 32 | 2.506,9 | 9.013 | 1.847,3 | 760,5 |
| Auswahlende | T | 32 | 2.012,6 | 8.811 | 1.353,0 | 760,4 |
| Desktop-Ruhe | R | 32 | 2.179,5 | 6.330 | 2.077,1 | 106,1 |
| Desktop-Ruhe | T | 32 | 1.173,2 | 6.529 | 1.069,5 | 107,4 |

Die ungefähr aktive glibc-Menge ist sehr ähnlich, der freie Arena-Bestand weicht
stark ab. Das passt zu einem Allokationshöchststand mit Retention/Fragmentierung,
nicht zu zusätzlichen lebenden Video-Bitmaps. Es ist kein Beweis, welcher Guard,
welcher native Thread oder welche Bibliothek die unterschiedliche Arena-Historie
verursacht. Dafür fehlen neue belastbare Allokationsstacks; die offene native
Profiler-Stabilität wird nicht durch weitere riskante Diagnosezugriffe umgangen.

`busy ≈ arena-current − fast/rest + glibc-mmap` ist eine virtuelle Allocatorgröße,
keine residente PSS. „Frei“ bedeutet ebenfalls nicht vollständig resident. Aggregate
und einzelne Heaps werden nicht doppelt gezählt. Die direkten UnmanagedBlob-
WriteableBitmap-Mappings liegen außerhalb dieser glibc-Statistik; andere Skia-Bilder,
Snapshots, Kopien und GPU-Treiberpfade sind mit den Zählern nicht lückenlos
zugeordnet. UI-, Plattform- und Renderreferenzen auf dieselben Pixel werden nicht
als mehrere Pixelallokationen addiert. Es gibt weder malloc_trim noch erzwungene GC
oder Allokator-Overrides; die aufgezeichnete Prozessumgebung enthält keine solchen
MALLOC-/GLIBC-Tunables.


### Verbleibender Quellstand, Nutzen und Grenzen

Der vorgeschlagene Stand verändert nur zwei Produktivdateien:

- `Extensions/CrossfadeImage.cs:390` und `:624`: Clear/Hide erst bei tatsächlich
  beendeter Opazitätsanimation und passender aktueller Generation. Die bisherigen
  beiden Bild-Leases bleiben für schnelle Rückkehr erhalten; Clear/Detach und
  tatsächlicher Ersatz geben sie wie bisher frei.
- `Helpers/Video/VideoSurfaceControl.cs:57`, `:82`, `:99`, `:114`: eindeutig gepaarte
  Frame-Subscriptions und Surface-Generationen; keine Präsentationsallokation vor
  Attach und keine Wiederbelebung durch alte Detached-/Surface-Callbacks.
- Dieselbe Datei `:136`, `:206`, `:210`, `:228`: bestehende kompatible Bitmap im
  eigenen Slot weiterverwenden, neue Speicher erst nach erfolgreicher Kopie zeichnen,
  inkompatible/ersetzte/detachte Speicher sicher disposen. Aufgezeichnete Render-
  Referenzen dürfen die native Ressource bis zum Ende ihrer Arbeit halten.

Der Never-Attached-Test weist tatsächlich null Subscription/Bitmap vor Attach und
normale Wiedergabe nach Attach nach. Im gewöhnlichen Navigator-Messpfad empfängt
kein unangehängter Hintergrundslot Frames; deshalb ist dort kein eigenständiger
PSS-Gewinn dieses Guards messbar. Eine bei einem solchen Slot verhinderte
4K-Präsentationsallokation entspräche etwa 31,904 MiB logischen Pixeln. Das ist ein
bedingtes Potenzial und kein in der normalen Serie erzielter Prozessgewinn.

Alle Pixelmengen bleiben beschränkt wie in d8fa12f. Endgültig unsichtbare, aber
weiter angehängte Slots werden im Rückfallstand weiterhin bis Surface-/Größen-
wechsel oder Detach gehalten; die verworfene frühe Freigabe bei Kanalende wird
nicht als umgesetzt ausgegeben. Es gibt keinen neuen globalen Pool, keine weitere
Vorhaltefrist und keinen Freigabetimer für Video. Die ursprüngliche Bild- und
Videoqualität, Ausgabedimensionen, Fadedauern, Audio-Workarounds, Cache-Limits,
Dekodierparallelität, gemeinsamen Bildaufträge und Preloads werden nicht verändert.
`LibVlcVideoSurface`, beide ViewModel-Dateien und der Video-Crossfade-Control sind
bytegleich mit d8fa12f. Die zusätzliche Playback-Lifetime existiert ausschließlich
in den reproduzierbaren verworfenen Varianten, nicht im Produktivprojekt.

Die Sicherheitsverbesserungen lassen sich damit unabhängig von einer PSS-Senkung
begründen: spätere Animationen dürfen keine neuen Quellen leeren/verstecken,
retirierte Controls dürfen keine Bitmap erneut anlegen, neue Pixel dürfen nicht
ungefüllt gezeichnet und aufgezeichnete Renderreferenzen nicht vorzeitig zerstört
werden. Ein großer allgemeiner aktiver Pixelgewinn gegenüber A wird nicht behauptet.
Der belastbare Traffic-Gewinn liegt gegenüber der verworfenen sofortigen Freigabe B.

### Weitere Risiken und Empfehlung zur gemeinsamen Bewertung

Die virtuelle QHD-KWin/Xwayland-Sitzung ersetzt keine physische Wayland-Abnahme.
Compositor-Pacing, seltene sichtbare schwarze Frames/Flackern bei produktiven Medien
und hörbare Audioqualität sind nicht durch die skalaren Zähler bewiesen. Automatische
First-Frame-, Richtungswechsel-, Größenwechsel-, Renderreferenz- und Native-
Lebenszyklustests sowie die unveränderten etablierten VM-Übergänge begrenzen das
Risiko; ein abschließender visueller/hörbarer Test bleibt erforderlich.

Die absolute PSS ist weiter kein verlässlicher Ersatz für aktive Ressourcen.
Schon A hat je nach separatem Block breite Verteilungen. Ein positiver Viererblock
ist kein allgemeiner PSS-Sparnachweis, ein einzelnes langes Ruhepaar keine statistisch
enge Nichtunterlegenheitsprüfung. Solche Folgewerte bleiben vollständig im Bericht
und in den Daten enthalten. Native Bereiche ohne zuverlässige Zuordnung bleiben
offen; insbesondere lässt sich die Differenz im freien Arena-Bestand nicht allein
den zusätzlich getesteten Guards kausal zuschreiben.

Die Ziele 1.024 MiB im BigMode und 800 MiB nach Desktop-Rückkehr werden für 4K
weiter verfehlt. 720p und Prism liegen in dieser Serie darunter. Bei 1080p und Mix
ist bereits der Übergangspeak höher als 1.024 MiB, ihre Desktop-PSS liegt über
800 MiB. Weder Cache-Vergrößerung noch Qualitätsreduktion, GC/trim oder globale
Allokator-Einstellungen werden zur Zielerfüllung eingesetzt.

Empfehlung: ursprüngliche begrenzte Video- und Bild-Wiederverwendung erhalten und
den kleinen T-Sicherheitsstand gemeinsam beurteilen. Vor einer erneuten frühen
Bitmap-Freigabe oder einem zweiten Playback-Zustandsprotokoll sollten deren
reproduzierten PSS-Nachteile verstanden und mit stabilen nativen Allokationsstacks
abgesichert werden. Die bestehende CPU-Pixel-/GPU-/Allocator-Trennung und die Tests
sind dabei beizubehalten. Der spätere Ansatz einer an der sichtbaren physischen
Theme-Fläche orientierten VLC-Ausgabe bleibt wie im vorherigen Abschnitt getrennt
von Decoder- und Avalonia-Renderauflösung dokumentiert und **nicht implementiert**.
Etappen 4 und 5 werden nicht begonnen.

Lokal dauerhaft abgelegte Reproduktionsquellen und kompakte Ergebnisdaten liegen unter
`benchmarks/video-slot-lifetime/`: Variantenpatches und Builder, bytegeprüfter
Fixture-Generator, beide externen Hooks, QHD-Runner, Auswertung, Source-/Medien-/
Build-/Hook-Hashes, Reihenfolgen, vollständige Ausschlüsse, prozessweise PSS-/CPU-/
Ressourcen-/Arena-Checkpoints und Funktionsergebnisse. `results/processes.json`
enthält alle angenommenen Messprozesse, getrennt nach Kohorte/Beobachter/Plan;
`results/resources.md` vermeidet Mehrfachzählung derselben Pixel. Große Rohtraces,
Medien, Profile und Builds bleiben in ignorierten Arbeitsverzeichnissen und werden
nicht als Repository-Inhalt vorgeschlagen. Die Anwendung lief für die Messungen ausschließlich mit
eigenen synthetischen Profilen. Die bestehende Bibliothek wurde nicht verändert;
die zuvor gesicherten Hashes der beiden vorhandenen Profil-JSONs bleiben unverändert.


### Zusätzliche Fünf-Minuten-Desktop-Ruhe

Je ein weiterer frischer Prozess, Reihenfolge T–A, sonst identische 4K-Folge und
drei BigMode-Sitzungen. Die eigene Kohorte `long-idle` wird nicht mit den
20-Sekunden-Endpunkten gepoolt. Alle Messprozesse enden mit Exit 0 ohne Hookfehler.

| PSS, MiB | A | T |
|---|---:|---:|
| Auswahlende | 2.168,5 | 2.118,5 |
| Übergangspeak / Prozesspeak | 2.508,0 | 2.552,2 |
| Erste Rückkehr + 3 s | 1.351,3 | 1.569,6 |
| Nach allen Sitzungen + 3 s | 1.436,6 | 1.667,4 |
| Nach zusätzlichen 300 s Ruhe | 1.440,5 | 1.675,3 |

| Zusätzliche Desktop-Ruhe | A PSS, MiB | T PSS, MiB |
|---|---:|---:|
| ca. 0 s | 1.436,6 | 1.667,4 |
| ca. 20 s | 1.436,5 | 1.672,1 |
| ca. 60 s | 1.440,5 | 1.675,4 |
| ca. 120 s | 1.440,5 | 1.675,3 |
| ca. 300 s | 1.440,5 | 1.675,3 |

In beiden Prozessen sind während **aller** Ruhe-Stichproben beobachtete Player,
zugewiesene Medien, Retirement-Einträge, Callback-Puffer und laufende Abbauten null.
T liegt nach fünf Minuten 234,8 MiB über A. Der Abstand besteht bereits nach dem
Abbau; innerhalb der Ruhephase wächst er nicht laufend weiter. Das ist kein
Nachweis eines sichtbaren Ressourcenlecks und dennoch ein relevanter Gegenbefund
zum günstigeren regulären Viererblock. Er wird nicht durch dessen Mediane verdeckt.

Das Einzelpaar liegt innerhalb der breiten historischen A-Verteilungen. Es belegt
weder eine allgemeine Regression noch eine verlässliche Nichtunterlegenheit von T.
Eine uneingeschränkte PSS-Abnahme oder Produktivfreigabe als Speicheroptimierung
wird deshalb ausdrücklich **nicht** erklärt. Als Rückfall bleiben die ursprüngliche
Bitmap-Wiederverwendung und nur die separat funktional abgesicherten kleinen
Sicherheitsguards zur gemeinsamen Bewertung stehen. Die PSS-Ziele bleiben offen.

Die kompakte Zeitreihe bis 300 s und die Null-Besitzer-Prüfung sind zusätzlich in
`results/processes.json` gespeichert. Große CSV-/JSONL-Rohdaten verbleiben außerhalb
des Repository-Inhalts.


### Abschließende Validierung und Übergabe

- `dotnet build Retromind.csproj -c Release --no-restore --verbosity quiet`: bestanden,
  0 Warnungen, 0 Fehler.
- Vollständige `Retromind.Tests`-Suite mit `--no-build`: 2.580 bestanden,
  0 fehlgeschlagen, 0 übersprungen; separates SSD-TMPDIR für die Test-Fixtures.
- Finale gezielte Suite mit Filter
  `FullyQualifiedName~CrossfadeImageTests|FullyQualifiedName~VideoPresentationLifetimeTests`:
  27 bestanden, einschließlich nochmals verstärktem nichtleerem Renderpfad vor
  fehlgeschlagener erster Framekopie. Produktivquellen seit der Messung unverändert.
- 785 normalisierte IL-Methoden des A-Builds gegen frischen d8fa12f identisch;
  finale T-Quellhashes stimmen mit dem gemessenen isolierten Build überein.
- Alle 28 letzten A/T-/R-Diagnose-/Funktions-/Langruheprozesse mit Exit 0 ohne
  Hookfehler; insgesamt 133 Messdatensätze plus sieben Funktions-/Stabilitätsläufe dauerhaft gesichert.
  Vollständige Ausschlüsse früherer Harness-/Build-Überlappungen sind separat erfasst.
- Python-Skripte kompilieren; `git diff --check` ohne Befund. Vorhandene Profil-JSONs
  stimmen weiterhin mit den vorab erfassten Hashes überein. Die virtuelle
  Testsitzung wurde beendet.

Etappe 3b ist als Untersuchung des Ressourcenlebenszyklus abgeschlossen. Ein
allgemeiner PSS-Vorteil ist nicht nachgewiesen. Der übernommene Sicherheitsstand
T behält die ursprüngliche, begrenzte A/B-Wiederverwendung und die separat
geprüften Animations-, Surface- und Renderguards. Der finale Diff wurde gegen
die gemessenen T-Quellhashes geprüft. Die beiden Produktivdateien sind bytegleich
mit dem Messstand. LibVLC, Video-Crossfade und ViewModel-Pfade entsprechen
weiterhin `d8fa12f` einschließlich des Abbaus aus Etappe 1. Die physische
Wayland-/Audioabnahme und der frühere native Absturzbefund bleiben offen.

### Test-AppImage und Paketprüfung

Das mit dem regulären Debian-Docker-Build erzeugte Test-AppImage liegt lokal
unter `dist/Retromind-0.2.5-alpha-x86_64.AppImage`. SHA-256
`0bd140c289b59ab309947cfdeb8b683e2898d9fc8072b08b9b4ed17c821df081`.
Das Manifest dokumentiert `d8fa12f` mit den damals noch uncommitteten T- und
Packaging-Änderungen. Die Wiederholung der vollständigen Testsuite mit den
Packaging-Anpassungen ergab 2.580 bestandene Tests und keine Fehler oder Skips.

MSBuild schließt Benchmarks, Tests und Entwicklungsdokumentation aus seinen
Standard-Items aus. Der Docker-Kontext übernimmt diese Verzeichnisse ebenfalls
nicht. `--appimage-extract` und die Prüfung der internen .NET-Bundle-Tabelle
erfassten 64 Dateien im Publish-Ausgabeverzeichnis, 221 Bundle-Einträge und 996
Dateien im entpackten AppImage. Darin und in beiden AppImage-Manifesten waren
keine Benchmarkdateien, Testartefakte oder Entwicklungsdokumente enthalten.
Die Markdown-Dateien im AppImage sind Theme-Anleitungen und Credits sowie
Lizenzhinweise. Diese Dateien gehören zu den ausgelieferten Themes und Komponenten.
Die ELF-Prüfung des regulären Verpackungsbuilds war erfolgreich.

`/benchmarks/` ist in Git ignoriert und war vor der Änderung nicht im Index.
Skripte, Variantenpatches und kompakte Daten bleiben lokal unter
`benchmarks/video-slot-lifetime/` erhalten. Der AppImage-Build erneuert nur
`.build-work/appimage/`, damit andere lokale Messverzeichnisse erhalten bleiben.
Der diagnostische Allokatorversuch und die Desktop-Analyse sind getrennte
Untersuchungen. Dieses AppImage dient der manuellen Prüfung von Audio, Crossfades,
Navigation, Themewechseln und AttractMode.
