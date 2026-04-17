using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;   
using System.IO.Compression;


// 因为FFmpeg的神秘小巧思，如果裁剪点不在帧头上，可能会补齐静音导致无法对其音频
// 问了ai也没招了(debug了1小时，呜呜呜
// 用MP3得从总时长反推每个谱面的起始偏移，我懒
// 所以这里的处理是改成ogg，然后算实际物理长度(具体为什么请看下面)。导致一堆屎山

// [屎山注意]
// CropAudio 和 AddFade 强制指定了 -ar 44100 -ac 2 -c:a pcm_s16le，这是为了保证所有片段采样率、声道数一致，避免 concat 失败。
// GetAudioDuration 仍然复用 ffmpeg 解析 Duration 字符串，但对于 WAV 文件是精确的。
// 之前用的WAV解码后长度可能有微小变化，所以才用实际长度累加，不过写了都写了，接着用吧。
// 因为红线的处理是即时的，所以加绿线我是先预处理了
// 感谢 gemini 大人的倾城协助
namespace DanMerger
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== 段位谱合并工具ver 1.02 ===");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("项目链接:https://github.com/YakumoZn/ChewCombine/");
            Console.WriteLine("write by.Chewwwwwwyaaaaaaa ===\n");
            Console.ResetColor();
            try
            {

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string songsDir = Path.Combine(baseDir, "songs");
            string relaxDir = Path.Combine(baseDir, "relax");
            string imgDir = Path.Combine(baseDir, "img");
            string createDir = Path.Combine(baseDir, "Create");

            // 如果没有文件就初始化
            if (!Directory.Exists(songsDir)) Directory.CreateDirectory(songsDir);
            if (!Directory.Exists(relaxDir)) Directory.CreateDirectory(relaxDir);
            if (!Directory.Exists(imgDir)) Directory.CreateDirectory(imgDir);
            if (!Directory.Exists(createDir)) Directory.CreateDirectory(createDir);

            // 清理上次遗留的 dan.osu 和 dan.wav
            string oldOsu = Path.Combine(createDir, "dan.osu");
            string oldOgg = Path.Combine(createDir, "dan.ogg");
            if (File.Exists(oldOsu)) File.Delete(oldOsu);
            if (File.Exists(oldOgg)) File.Delete(oldOgg);

            // 检查必要文件是否存在
            if (!File.Exists(Path.Combine(imgDir, "bg.png")))
            {
                Console.WriteLine("错误: img/bg.png 不存在，请放入背景图片");
                return;
            }

            string[] restFiles = Directory.GetFiles(relaxDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".mp3") || f.EndsWith(".ogg") || f.EndsWith(".wav")).ToArray();
            if (restFiles.Length != 1)
            {
                Console.WriteLine("错误: relax 文件夹必须包含且只包含一个音频文件（休息段）");
                return;
            }
            string restAudio = restFiles[0];

            // 继续原有逻辑，获取 songs 下的子文件夹等...
            var folders = Directory.GetDirectories(songsDir)
                .Select(d => new { Path = d, Name = Path.GetFileName(d) })
                .Where(x => int.TryParse(x.Name, out _))
                .OrderBy(x => int.Parse(x.Name))
                .ToList();
            if (folders.Count == 0) { Console.WriteLine("错误: songs 下没有数字命名的子文件夹"); return; }

            List<MapInfo> maps = new List<MapInfo>();

            foreach (var folder in folders)
            {
                Console.WriteLine($"\n处理谱面 {folder.Name} ...");

                string[] osuFiles = Directory.GetFiles(folder.Path, "*.osu", SearchOption.TopDirectoryOnly);
                if (osuFiles.Length != 1) { Console.WriteLine($"错误: 文件夹 {folder.Name} 必须包含一个 .osu 文件"); return; }
                string osuPath = osuFiles[0];

                string audioFile = GetAudioFromOsu(osuPath, folder.Path);
                if (audioFile == null) { Console.WriteLine($"错误: 无法从 {osuPath} 解析音频文件"); return; }

                long startMs = 0, endMs = 0;
                while (true)
                {
                    Console.Write("请输入裁剪起止时间（格式 00:00:000 03:30:681）: ");
                    string input = Console.ReadLine().Trim();
                    string[] parts = input.Split(' ');
                    if (parts.Length != 2)
                    {
                        Console.WriteLine("格式错误，需要两个时间，用空格分隔");
                        continue;
                    }
                    if (!TryParseTime(parts[0], out startMs) || !TryParseTime(parts[1], out endMs))
                    {
                        Console.WriteLine("时间格式错误，请用 00:00:000 格式");
                        continue;
                    }
                    if (startMs >= endMs)
                    {
                        Console.WriteLine("开始时间必须小于结束时间");
                        continue;
                    }
                    break;
                }

                maps.Add(new MapInfo
                {
                    FolderName = folder.Name,
                    OsuPath = osuPath,
                    AudioPath = audioFile,
                    StartMs = startMs,
                    EndMs = endMs
                });
            }

            // 因为屎山原因先预处理
            for (int i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                (map.r_Length, map.BaseBPM) = GetMapLength(map.OsuPath, map.StartMs, map.EndMs);
                Console.WriteLine($"谱面 {map.FolderName}: 时长 {map.r_Length} ms, BPM = {map.BaseBPM:F2}");
            }

            // 找出主 BPM（时长最长的谱面的 BPM）
            double masterBPM = 120;
            long maxLength = 0;
            foreach (var map in maps)
            {
                if (map.r_Length > maxLength)
                {
                    maxLength = map.r_Length;
                    masterBPM = map.BaseBPM;
                }
            }
            // Console.WriteLine($"debug: masterBPM = {masterBPM:F2} \n");



                string tempDir = Path.Combine(Path.GetTempPath(), "DanMerger_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            Console.WriteLine("\n开始处理音频...");
            List<string> audioSegments = new List<string>();
            long currentOffset = 0;
            List<TimingPoint> allTimingPoints = new List<TimingPoint>();
            List<HitObject> allHitObjects = new List<HitObject>();

            for (int i = 0; i < maps.Count; i++) //处理音频
            {
                var map = maps[i];
                Console.WriteLine($"处理谱面 {map.FolderName} ...");

                // 裁剪音频
                string cropped = Path.Combine(tempDir, $"map_{i}_cropped.wav");
                CropAudio(map.AudioPath, cropped, map.StartMs, map.EndMs);
                // 加淡入淡出
                string faded = Path.Combine(tempDir, $"map_{i}_faded.wav");
                AddFade(cropped, faded, 1000, 1000);
                audioSegments.Add(faded);

                // 解析谱面
                ParseOsu(map.OsuPath, out var timings, out var hits);

                // 打印当前偏移量（累加前）
                Console.WriteLine($"  当前偏移量 (累加前): {currentOffset} ms");

                // 偏移 TimingPoints
                foreach (var tp in timings)
                {
                    if (tp.Time >= map.StartMs && tp.Time <= map.EndMs)
                    {
                        tp.Time = currentOffset + (tp.Time - map.StartMs);
                        allTimingPoints.Add(tp);
                    }
                }
                // 因为可能会有没有红线的情况(因为note结束实际和音频结束时间不一定是一样的)没有则在起始处重添加一条
                long firstRedTime = -1;
                bool hasRed = false;

                // 先检查裁剪区间内是否有红线
                foreach (var tp in timings)
                {
                    if (tp.Time >= map.StartMs && tp.Time <= map.EndMs && tp.Uninherited == 1)
                    {
                        hasRed = true;
                        firstRedTime = currentOffset + (tp.Time - map.StartMs);
                        break;
                    }
                }

                // 如果没有红线，则添加默认红线（取原谱面的第一个红线 BPM，否则 120）
                if (!hasRed)
                {
                    double defaultBeatLength = 500; // 120 BPM
                    foreach (var tp in timings)
                    {
                        if (tp.Uninherited == 1)
                        {
                            defaultBeatLength = tp.BeatLength;
                            break;
                        }
                    }
                    var defaultRed = new TimingPoint
                    {
                        Time = currentOffset,
                        BeatLength = defaultBeatLength,
                        Meter = 4,
                        SampleSet = 0,
                        SampleIndex = 0,
                        Volume = 100,
                        Uninherited = 1,
                        Effects = 0
                    };
                    allTimingPoints.Add(defaultRed);
                    firstRedTime = currentOffset;
                    Console.WriteLine($"{map.FolderName} 无红线，已在 {currentOffset} ms 处添加默认红线");
                }

                    // 加绿线
                    // 倍率 = 主BPM / 谱面BPM
                    double ratio = masterBPM / map.BaseBPM;
                    double greenBeatLength = -100 / ratio;
                    var greenLine = new TimingPoint
                    {
                        Time = firstRedTime,
                        BeatLength = greenBeatLength,
                        Meter = 4,
                        SampleSet = 0,
                        SampleIndex = 0,
                        Volume = 100,
                        Uninherited = 0,
                        Effects = 0
                    };
                    allTimingPoints.Add(greenLine);
                    Console.WriteLine($"  绿线: 时间 {firstRedTime} ms, 倍率 {ratio:F2} (主BPM {masterBPM:F2} / 谱面BPM {map.BaseBPM:F2})");



                    // 偏移 HitObjects
                    foreach (var hit in hits)
                {
                    if (hit.StartTime >= map.StartMs && hit.StartTime <= map.EndMs)
                    {
                        hit.StartTime = currentOffset + (hit.StartTime - map.StartMs);
                        if (hit.IsLong)
                            hit.EndTime = currentOffset + (hit.EndTime - map.StartMs);
                        allHitObjects.Add(hit);
                    }
                }

                // 累加当前谱面片段的实际物理时长（读取 faded 文件）
                long actualMapLen = GetAudioDuration(faded);
                currentOffset += actualMapLen;
                Console.WriteLine($"  谱面片段理论时长: {map.EndMs - map.StartMs} ms, 实际时长: {actualMapLen} ms, 累加后偏移量: {currentOffset} ms");

                // 如果不是最后一个谱面，处理休息段
                if (i < maps.Count - 1)
                {
                    long restFullDuration = GetAudioDuration(restAudio);
                    string restCopy = Path.Combine(tempDir, $"rest_{i}_copy.wav");
                    CropAudio(restAudio, restCopy, 0, restFullDuration);
                    string restFaded = Path.Combine(tempDir, $"rest_{i}_faded.wav");
                    AddFade(restCopy, restFaded, 1000, 1000);
                    audioSegments.Add(restFaded);

                    // 获取实际物理时长
                    long actualRestLen = GetAudioDuration(restFaded);
                    currentOffset += actualRestLen;
                    Console.WriteLine($"  休息段理论时长: {restFullDuration} ms, 实际时长: {actualRestLen} ms, 累加后偏移量: {currentOffset} ms");
                }

                Console.WriteLine(); // 空行分隔
            }

            Directory.CreateDirectory(createDir);
            string finalAudio = Path.Combine(createDir, "dan.ogg");
            ConcatAudio(audioSegments, finalAudio);
            Console.WriteLine($"音频生成: {finalAudio}");

            string finalOsu = Path.Combine(createDir, "dan.osu");
            GenerateOsu(finalOsu, finalAudio, allTimingPoints, allHitObjects, Path.Combine(imgDir, "bg.png"));
            Console.WriteLine($"谱面生成: {finalOsu}");

            string oszName = GetNextOszNumber(createDir);
            string oszPath = Path.Combine(createDir, oszName);
            PackToOsz(finalAudio, finalOsu, Path.Combine(imgDir, "bg.png"), oszPath);
            Console.WriteLine($"打包完成: {oszPath}");

            try { Directory.Delete(tempDir, true); } catch { }

            Console.WriteLine("\n全部完成！按任意键退出...");
        }
            //防报错
            catch (Exception ex)
            {
                Console.WriteLine($"\n发生错误: {ex.Message}");
                Console.WriteLine("详细信息：");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                Console.WriteLine("\n按回车键退出...");
                Console.ReadLine();
            }
                //Console.ReadKey();
        }

        static bool TryParseTime(string timeStr, out long ms)
        {
            ms = 0;
            var parts = timeStr.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out int min)) return false;
            if (!int.TryParse(parts[1], out int sec)) return false;
            if (!int.TryParse(parts[2], out int millis)) return false;
            ms = min * 60 * 1000 + sec * 1000 + millis;
            return true;
        }

        static string GetAudioFromOsu(string osuPath, string folderPath)
        {
            foreach (string line in File.ReadLines(osuPath))
            {
                if (line.StartsWith("AudioFilename:"))
                {
                    string filename = line.Substring("AudioFilename:".Length).Trim();
                    string fullPath = Path.Combine(folderPath, filename);
                    if (File.Exists(fullPath)) return fullPath;
                    return null;
                }
            }
            return null;
        }

        static void RunFFmpeg(string args)
        {
            string genmulu = AppDomain.CurrentDomain.BaseDirectory; //根目录
            string ffmpegPath = Path.Combine(genmulu, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                ffmpegPath = Path.Combine(genmulu, "ffmpeg", "ffmpeg.exe");
                //Console.WriteLine($"debug: ffmpeg 路径: {ffmpegPath}");

                // 我是傻逼
            }
            //if (!File.Exists(ffmpegPath))
            //{
            //    throw new Exception("ffmpeg.exe 不存在，请放到程序同目录下");
            //}
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {

                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {

                    //Console.WriteLine("debug :：\n" + err);
                    throw new Exception($"FFmpeg 错误: {err}");
                }
            }
        }

        static void CropAudio(string input, string output, long startMs, long endMs)
        {
            double startSec = startMs / 1000.0;
            double durationSec = (endMs - startMs) / 1000.0;
            // 强制指定采样率 -ar 44100 和双声道 -ac 2
            RunFFmpeg($"-ss {startSec} -t {durationSec} -i \"{input}\" -ar 44100 -ac 2 -c:a pcm_s16le \"{output}\"");
        }

        static void AddFade(string input, string output, int fadeInMs, int fadeOutMs)
        {
            double fadeIn = fadeInMs / 1000.0;
            double totalDur = GetAudioDuration(input) / 1000.0;
            double fadeOutStart = totalDur - (fadeOutMs / 1000.0);
            // 同样强制指定，防止格式丢失
            RunFFmpeg($"-i \"{input}\" -af \"afade=t=in:st=0:d={fadeIn},afade=t=out:st={fadeOutStart}:d={fadeOutMs / 1000.0}\" -ar 44100 -ac 2 -c:a pcm_s16le \"{output}\"");
        }

        static long GetAudioDuration(string audioPath)
        {
            // 依旧找子文件夹
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                ffmpegPath = Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
            }
            if (!File.Exists(ffmpegPath))
            {
                throw new Exception("找不到 ffmpeg.exe");
            }

            // 使用ffmpeg读取音频时长 输出到stderr 解析duration
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{audioPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardError.ReadToEnd();
                p.WaitForExit();
                // 在输出中查找 Duration: 00:00:12.34 这样的行
                var match = Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d+)");
                if (match.Success)
                {
                    int hours = int.Parse(match.Groups[1].Value);
                    int minutes = int.Parse(match.Groups[2].Value);
                    double seconds = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    return (long)((hours * 3600 + minutes * 60 + seconds) * 1000);
                }
            }
            return 0;
        }

        static void ConcatAudio(List<string> files, string output)
        {
            string listFile = Path.GetTempFileName();
            File.WriteAllLines(listFile, files.Select(f => $"file '{f}'"));
            // 输出为OGG 质量 -q:a 3
            RunFFmpeg($"-f concat -safe 0 -i \"{listFile}\" -c:a libvorbis -q:a 3 \"{output}\"");
            File.Delete(listFile);
        }

        static void ParseOsu(string osuPath, out List<TimingPoint> timings, out List<HitObject> hits)
        {
            timings = new List<TimingPoint>();
            hits = new List<HitObject>();
            string currentSection = "";

            foreach (string line in File.ReadLines(osuPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentSection = trimmed;
                    continue;
                }

                if (currentSection == "[TimingPoints]" && !string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("//"))
                { // 记录当前所在的节（找如 [TimingPoints]等tag）,为什么请看下面
                  // 笑点解析，之前这里直接暴力搜索导致把背景图"0,0,"Kano.jpg",0,0"也当note算进去了
                    string[] parts = trimmed.Split(',');
                    if (parts.Length >= 2 && long.TryParse(parts[0], out long time))
                    {

                        timings.Add(new TimingPoint
                        {
                            Time = time,
                            BeatLength = double.Parse(parts[1]),
                            Meter = parts.Length > 2 ? int.Parse(parts[2]) : 4,
                            SampleSet = parts.Length > 3 ? int.Parse(parts[3]) : 0,
                            SampleIndex = parts.Length > 4 ? int.Parse(parts[4]) : 0,
                            Volume = parts.Length > 5 ? int.Parse(parts[5]) : 100,
                            Uninherited = parts.Length > 6 ? int.Parse(parts[6]) : 1,
                            Effects = parts.Length > 7 ? int.Parse(parts[7]) : 0
                        });
                    }
                }
                else if (currentSection == "[HitObjects]" && !string.IsNullOrWhiteSpace(trimmed))
                {
                    string[] parts = trimmed.Split(',');
                    if (parts.Length >= 6)
                    {
                        int x = int.Parse(parts[0]);
                        int y = int.Parse(parts[1]);
                        long start = long.Parse(parts[2]);
                        int type = int.Parse(parts[3]);
                        int hitSound = int.Parse(parts[4]);
                        string extras = parts[5];
                        bool isLong = (type & 128) != 0;
                        long endTime = start;
                        if (isLong && extras.Contains(':'))
                        {
                            long.TryParse(extras.Split(':')[0], out endTime);
                        }
                        hits.Add(new HitObject
                        {
                            X = x,
                            Y = y,
                            StartTime = start,
                            Type = type,
                            HitSound = hitSound,
                            IsLong = isLong,
                            EndTime = endTime,
                            Extras = extras
                        });
                    }
                }
            }
        }
        static (long duration, double bpm) GetMapLength(string osuPath, long startMs, long endMs)
        {
            long duration = endMs - startMs;
            double bpm = 120.0;

            ParseOsu(osuPath, out var timings, out _);
            // 优先找裁剪区间内的红线
            foreach (var tp in timings)
            {
                if (tp.Time >= startMs && tp.Time <= endMs && tp.Uninherited == 1)
                {
                    bpm = 60000.0 / tp.BeatLength;
                    return (duration, bpm);
                }
            }
            // 区间内没有，则找第一个红线
            foreach (var tp in timings)
            {
                if (tp.Uninherited == 1)
                {
                    bpm = 60000.0 / tp.BeatLength;
                    return (duration, bpm);
                }
            }
            // 防意外，啥也没有使用120，不然会崩溃
            return (duration, bpm);
        }

        // 纯手工初始化谱面文件, 不嘻嘻(指.osu
        static void GenerateOsu(string outputPath, string audioPath, List<TimingPoint> timings, List<HitObject> hits, string bgPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("osu file format v14");
            sb.AppendLine();
            sb.AppendLine("[General]");
            sb.AppendLine($"AudioFilename: {Path.GetFileName(audioPath)}");
            sb.AppendLine("AudioLeadIn: 0");
            sb.AppendLine("PreviewTime: -1");
            sb.AppendLine("Countdown: 0");
            sb.AppendLine("SampleSet: None");
            sb.AppendLine("StackLeniency: 0.7");
            sb.AppendLine("Mode: 3");
            sb.AppendLine("LetterboxInBreaks: 0");
            sb.AppendLine("SpecialStyle: 0");
            sb.AppendLine("WidescreenStoryboard: 0");
            sb.AppendLine();
            sb.AppendLine("[Editor]");
            sb.AppendLine("DistanceSpacing: 1");
            sb.AppendLine("BeatDivisor: 4");
            sb.AppendLine("GridSize: 8");
            sb.AppendLine("TimelineZoom: 2.5");
            sb.AppendLine();
            sb.AppendLine("[Metadata]");
            sb.AppendLine("Title:your dans");
            sb.AppendLine("TitleUnicode:your dans");
            sb.AppendLine("Artist:V.A");
            sb.AppendLine("ArtistUnicode:V.A");
            sb.AppendLine("Creator:your name");
            sb.AppendLine("Version:chew");
            sb.AppendLine("Source:");
            sb.AppendLine("Tags:");
            sb.AppendLine("BeatmapID:0");
            sb.AppendLine("BeatmapSetID:0");
            sb.AppendLine();
            sb.AppendLine("[Difficulty]");
            sb.AppendLine("HPDrainRate:8");
            sb.AppendLine("CircleSize:4");
            sb.AppendLine("OverallDifficulty:9");
            sb.AppendLine("ApproachRate:5");
            sb.AppendLine("SliderMultiplier:1.4");
            sb.AppendLine("SliderTickRate:1");
            sb.AppendLine();
            sb.AppendLine("[Events]");
            sb.AppendLine("//Background and Video events");
            sb.AppendLine($"0,0,\"bg.png\",0,0");
            sb.AppendLine("//Break Periods");
            sb.AppendLine("//Storyboard Layer 0 (Background)");
            sb.AppendLine("//Storyboard Layer 1 (Fail)");
            sb.AppendLine("//Storyboard Layer 2 (Pass)");
            sb.AppendLine("//Storyboard Layer 3 (Foreground)");
            sb.AppendLine("//Storyboard Layer 4 (Overlay)");
            sb.AppendLine("//Storyboard Sound Samples");
            sb.AppendLine();
            sb.AppendLine("[TimingPoints]");
            foreach (var tp in timings.OrderBy(t => t.Time))
            {
                sb.AppendLine($"{tp.Time},{tp.BeatLength},{tp.Meter},{tp.SampleSet},{tp.SampleIndex},{tp.Volume},{tp.Uninherited},{tp.Effects}");
            }
            sb.AppendLine();
            sb.AppendLine("[HitObjects]");
            foreach (var hit in hits.OrderBy(h => h.StartTime))
            {
                string extras = hit.Extras;
                if (hit.IsLong)
                    extras = $"{hit.EndTime}:0:0:0:0:";
                sb.AppendLine($"{hit.X},{hit.Y},{hit.StartTime},{hit.Type},{hit.HitSound},{extras}");
            }
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        static string GetNextOszNumber(string createDir)
        {
            int num = 1;
            while (File.Exists(Path.Combine(createDir, $"{num}.osz")))
                num++;
            return $"{num}.osz";
        }

        static void PackToOsz(string audioFile, string osuFile, string bgFile, string oszPath)
        {
            string tempZipDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempZipDir);
            File.Copy(audioFile, Path.Combine(tempZipDir, Path.GetFileName(audioFile)));
            File.Copy(osuFile, Path.Combine(tempZipDir, Path.GetFileName(osuFile)));
            File.Copy(bgFile, Path.Combine(tempZipDir, Path.GetFileName(bgFile)));
            ZipFile.CreateFromDirectory(tempZipDir, oszPath);
            Directory.Delete(tempZipDir, true);
        }
    }

    class MapInfo
    {
        public string FolderName;
        public string OsuPath;
        public string AudioPath;
        public long StartMs;
        public long EndMs;
        public long r_Length;// 裁剪后真实时长
        public double BaseBPM;
    }

    class TimingPoint
    {
        public long Time;
        public double BeatLength;
        public int Meter;
        public int SampleSet;
        public int SampleIndex;
        public int Volume;
        public int Uninherited;
        public int Effects;
    }

    class HitObject
    {
        public int X, Y;
        public long StartTime;
        public int Type;
        public int HitSound;
        public bool IsLong;
        public long EndTime;
        public string Extras;
    }
}