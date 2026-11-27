using System.Text;
using Gallery.Services;
namespace Gallery;
public partial class App
{
    /// <summary>同步进度：CLI 验收要「第 N 次回调立刻取消」，Progress&lt;T&gt; 是异步投递来不及。</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private bool RunDevelopmentCommand(string[] cli)
    {
        // --verify-db：批次 0 自动验收（读老库写报告后退出，不出窗口）
        if (cli.Contains("--verify-db"))
        {
            RunVerification();
            Exit();
            return true;
        }

        // --verify-video：沙箱内验收 mp4 扫描入库、媒体筛选与封面缓存（不出窗口）
        if (cli.Contains("--verify-video"))
        {
            RunVerifyVideo();
            Exit();
            return true;
        }

        // --verify-packs <路径>：沙箱内验收资料包导入（不出窗口，不碰真实目录）
        var vpk = Array.IndexOf(cli, "--verify-packs");
        if (vpk >= 0 && vpk + 1 < cli.Length)
        {
            try
            {
                Task.Run(() => RunVerifyPacks(cli[vpk + 1])).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppService.DebugLog("verify-packs failed: " + ex);
            }
            Exit();
            return true;
        }

        // --verify-query：批次 1 验收（8 键排序 + 搜索，写报告后退出）
        if (cli.Contains("--verify-query"))
        {
            var q = new StringBuilder();
            try
            {
                var store2 = new StoreService(AppPaths.DbPath);
                foreach (var sortKey in SortKeys.All)
                {
                    var rows2 = store2.Query(new StoreService.QuerySpec
                    {
                        Sort = sortKey, Desc = false, Limit = 5,
                    });
                    q.AppendLine($"sort={sortKey,-8} 前5: {string.Join(" | ", rows2.Select(r => r.Name))}");
                }
                var hits = store2.Query(new StoreService.QuerySpec
                {
                    Search = "2026-0", Sort = "name", Limit = 5,
                });
                q.AppendLine($"search=2026-0 命中 {hits.Count}（LIMIT5: {string.Join(" | ", hits.Take(5).Select(r => r.Name))}）");
                var byName = store2.Query(new StoreService.QuerySpec
                    { Search = "zenless", Sort = "name" });
                q.AppendLine($"search=zenless（标签名路径）命中 {byName.Count}（LIMIT5: {string.Join(" | ", byName.Take(5).Select(r => r.Name))}）");

                // 词库中文名也应该能搜到图（tag_profiles.zh/en）
                var byZh = store2.Query(new StoreService.QuerySpec
                    { Search = "伊埃斯", Sort = "name" });
                q.AppendLine($"search=伊埃斯（中文名）命中 {byZh.Count}；LIMIT3: {string.Join(" | ", byZh.Take(3).Select(r => r.Name))}");
            }
            catch (Exception ex) { q.AppendLine("ERROR: " + ex.Message); }
            var outDir3 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDir3);
            File.WriteAllText(Path.Combine(outDir3, "verify_query.txt"), q.ToString());
            Exit();
            return true;
        }

        // --verify-tags：阶段 C 词库 + 分类筛选验收（写报告后退出）
        if (cli.Contains("--verify-tags"))
        {
            var sb = new StringBuilder();
            try
            {
                var store3 = new StoreService(AppPaths.DbPath);
                var catalog = new TagCatalogService(store3);
                var reports = catalog.ImportAll(log: line => sb.AppendLine("IMPORT " + line));
                sb.AppendLine($"导入来源 {reports.Count} 个；示例：{string.Join(", ", reports.Take(3).Select(r => $"{r.SourceId}={r.Records}"))}");
                var (games, entities, names, sourceTags, memberships, imports) = catalog.Summary();
                sb.AppendLine($"词库计数 games={games} entities={entities} names={names} source_tags={sourceTags} memberships={memberships} imports={imports}");

                foreach (var needle in new[] { "genshin", "刻晴", "qingque", "wuthering", "伊埃斯", "Eous" })
                {
                    var hits = store3.SearchTags(needle, 5);
                    sb.AppendLine($"search_tags('{needle}') → {hits.Count}: " +
                        string.Join(" | ", hits.Select(t => $"{t.Category}/{t.Name}({t.Display})×{t.Count}")));
                }

                var all = store3.AllTags();
                var enriched = catalog.Enrich(all);
                sb.AppendLine($"标签总数 {all.Count}；词库补出中文名 {enriched.Count(t => t.ZhName.Length > 0)}；" +
                              $"分类分布 " + string.Join(", ", all.GroupBy(t => t.Category)
                                  .OrderByDescending(g => g.Count()).Take(8)
                                  .Select(g => $"{g.Key}={g.Count()}")));
                foreach (var t in enriched.Where(t => t.ZhName.Length > 0).Take(5))
                    sb.AppendLine($"  enrich {t.Category}/{t.Name} → {t.ZhName} / {t.EnName} @{t.EntityKey}");

                foreach (var needle in new[] { "刻晴", "qingque", "爱弥斯" })
                {
                    var hits = catalog.SearchEntities(needle, 3);
                    sb.AppendLine($"catalog_search('{needle}') → " +
                        string.Join(" | ", hits.Select(h => $"{h.Zh}/{h.En}({h.Kind}@{h.GameKey})")));
                }

                var lookupNames = all.Where(t => t.Aliases.Count > 0).Take(5).Select(t => t.Name).ToList();
                var profiles = catalog.Lookup(lookupNames);
                foreach (var (name, value) in profiles)
                    sb.AppendLine($"lookup('{name}') → zh='{value.Zh}' en='{value.En}' key='{value.EntityKey}'");

                // 分类筛选：OR / AND / 排除 / 未打标签
                var sample = store3.SearchTags("", 6);
                if (sample.Count >= 2)
                {
                    var a = sample[0].Id;
                    var b = sample[1].Id;
                    var orCount = store3.Query(new StoreService.QuerySpec { TagIds = [a, b], TagMode = "or" }).Count;
                    var andCount = store3.Query(new StoreService.QuerySpec { TagIds = [a, b], TagMode = "and" }).Count;
                    var excluded = store3.Query(new StoreService.QuerySpec { ExcludedTagIds = [a] }).Count;
                    var baseline = store3.Query(new StoreService.QuerySpec()).Count;
                    sb.AppendLine($"筛选 baseline={baseline} OR({sample[0].Name},{sample[1].Name})={orCount} AND={andCount} 排除{sample[0].Name}={excluded}");
                    var grouped = store3.Query(new StoreService.QuerySpec
                    {
                        TagGroups = new Dictionary<string, IReadOnlyList<int>> { ["work"] = [a] },
                        GroupModes = new Dictionary<string, string> { ["work"] = "all" },
                    }).Count;
                    sb.AppendLine($"分组筛选 work=all({sample[0].Name}) = {grouped}");
                }
                var untagged = store3.Query(new StoreService.QuerySpec { Untagged = true }).Count;
                sb.AppendLine($"未打标签图片 = {untagged}");
                store3.Dispose();
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            var outDir4 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDir4);
            File.WriteAllText(Path.Combine(outDir4, "verify_tags.txt"), sb.ToString());
            Exit();
            return true;
        }

        // --verify-nsfw：阶段 E 判定流水线验收（没有真模型时用替身打分器，验证写库/筛选/显示联动）
        if (cli.Contains("--verify-nsfw"))
        {
            var sbN = new StringBuilder();
            try
            {
                var storeN = new StoreService(AppPaths.DbPath);
                var deterministic = new NsfwService(storeN)
                {
                    // 按文件名散列造四类概率：偶数名偏 sfw，奇数名偏 explicit
                    OverrideScorer = (path, _) =>
                    {
                        var hash = Math.Abs(path.GetHashCode());
                        var bias = hash % 2 == 0;
                        return bias
                            ? new NsfwProbs(0.86, 0.08, 0.04, 0.02)
                            : new NsfwProbs(0.05, 0.10, 0.15, 0.70);
                    },
                };

                var run1 = deterministic.RunAsync(onlyStale: true).GetAwaiter().GetResult();
                sbN.AppendLine("第一次扫描：" + NsfwService.Describe(run1));
                sbN.AppendLine($"模型名={("(替身)")} 汇总前={storeN.NsfwSummary()}");

                var run2 = deterministic.RunAsync(onlyStale: true).GetAwaiter().GetResult();
                sbN.AppendLine("幂等复跑：" + NsfwService.Describe(run2));

                var run3 = deterministic.RunAsync(onlyStale: false).GetAwaiter().GetResult();
                sbN.AppendLine("强制全量：" + NsfwService.Describe(run3));

                var (scored, nsfw, unknown) = storeN.NsfwSummary();
                sbN.AppendLine($"汇总 scored={scored} nsfw={nsfw} unknown={unknown}");
                sbN.AppendLine("全部=" + storeN.Query(new StoreService.QuerySpec()).Count +
                               " 隐藏敏感=" + storeN.Query(new StoreService.QuerySpec { HideNsfw = true }).Count);

                var anyScore = storeN.NsfwScoreFor(
                    storeN.Query(new StoreService.QuerySpec()).FirstOrDefault()?.Id ?? 0);
                sbN.AppendLine(anyScore is null
                    ? "第一条没有分数"
                    : $"样本 id={anyScore.ImageId} source={anyScore.Source} rating={anyScore.Rating} " +
                      $"p={anyScore.NsfwProb:F2} conf={anyScore.Confidence:F2} margin={anyScore.Margin:F2} detail={anyScore.Detail}");
                storeN.Dispose();
            }
            catch (Exception ex) { sbN.AppendLine("ERROR: " + ex); }

            var outDirN = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDirN);
            File.WriteAllText(Path.Combine(outDirN, "verify_nsfw.txt"), sbN.ToString());
            Exit();
            return true;
        }

        // --verify-nsfw-cancel：验证「扫描可取消」——进度回调里取消，检查部分结果已落库、异常是取消
        if (cli.Contains("--verify-nsfw-cancel"))
        {
            var sbC = new StringBuilder();
            try
            {
                var storeC = new StoreService(AppPaths.DbPath);
                // 先清空评分，保证本轮确实要扫
                storeC.Exec("DELETE FROM nsfw_scores");
                storeC.Exec("UPDATE images SET nsfw=NULL");
                storeC.Commit();

                var svc = new NsfwService(storeC)
                {
                    OverrideScorer = (path, token) =>
                    {
                        System.Threading.Thread.Sleep(15);
                        token.ThrowIfCancellationRequested();
                        return new NsfwProbs(0.2, 0.2, 0.3, 0.3);
                    },
                };

                using var cts = new System.Threading.CancellationTokenSource();
                var seen = 0;
                var progress = new SyncProgress<(int Done, int Total)>(p2 =>
                {
                    if (++seen >= 3) cts.Cancel();
                });

                string outcome;
                try
                {
                    var r = svc.RunAsync(progress, onlyStale: false, cts.Token).GetAwaiter().GetResult();
                    outcome = "没有抛异常：" + NsfwService.Describe(r);
                }
                catch (OperationCanceledException)
                {
                    outcome = "按预期抛出 OperationCanceledException";
                }

                var (scoredC, nsfwC, _) = storeC.NsfwSummary();
                sbC.AppendLine("取消结果：" + outcome);
                sbC.AppendLine($"进度回调次数={seen} 已落库 scored={scoredC} nsfw={nsfwC}");
                sbC.AppendLine(scoredC > 0 ? "部分结果已保留 ✓" : "没有保留任何结果 ✗");
                storeC.Dispose();
            }
            catch (Exception ex) { sbC.AppendLine("ERROR: " + ex); }

            var outDirC = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDirC);
            File.WriteAllText(Path.Combine(outDirC, "verify_nsfw_cancel.txt"), sbC.ToString());
            Exit();
            return true;
        }

        // --verify-nsfw-preview：验证「重判定预览」——先用替身打一轮（旧结果），再用反向替身预览并应用
        if (cli.Contains("--verify-nsfw-preview"))
        {
            var sbP = new StringBuilder();
            try
            {
                var storeP = new StoreService(AppPaths.DbPath);

                // 旧结果：偶数名 sfw / 奇数名 explicit
                var first = new NsfwService(storeP)
                {
                    OverrideScorer = (path, _) => Math.Abs(path.GetHashCode()) % 2 == 0
                        ? new NsfwProbs(0.86, 0.08, 0.04, 0.02)
                        : new NsfwProbs(0.05, 0.10, 0.15, 0.70),
                };
                var runOld = first.RunAsync(onlyStale: false).GetAwaiter().GetResult();
                sbP.AppendLine("旧结果扫描：" + NsfwService.Describe(runOld));

                // 预览：全部反转（sfw <-> explicit），应当每张都变
                var flipped = new NsfwService(storeP)
                {
                    OverrideScorer = (path, _) => Math.Abs(path.GetHashCode()) % 2 == 0
                        ? new NsfwProbs(0.05, 0.10, 0.15, 0.70)
                        : new NsfwProbs(0.86, 0.08, 0.04, 0.02),
                };

                var (err1, rows1) = flipped.PreviewAsync(500).GetAwaiter().GetResult();
                sbP.AppendLine($"预览错误=[{err1}] 行数={rows1.Count} 会变={rows1.Count(r => r.Changed)} " +
                               $"变敏感={rows1.Count(r => r.Changed && r.IsNsfw)} 变普通={rows1.Count(r => r.Changed && !r.IsNsfw)}");
                foreach (var row in rows1.Take(3))
                    sbP.AppendLine($"  例：id={row.ImageId} {row.OldRating} -> {row.Rating} " +
                                   $"nsfw={row.Probs.NsfwProb:F2} conf={row.Confidence:F2}");

                // 预览不该写库：此时库里的分级应当还是旧的
                sbP.AppendLine("应用前汇总：" + storeP.NsfwSummary());

                var applied = flipped.ApplyRows(rows1, tagIt: false);
                sbP.AppendLine($"应用 {applied} 行；应用后汇总：{storeP.NsfwSummary()}");

                var (_, rows2) = flipped.PreviewAsync(500).GetAwaiter().GetResult();
                sbP.AppendLine($"复跑预览：行数={rows2.Count} 会变={rows2.Count(r => r.Changed)}（应为 0）");
            }
            catch (Exception ex)
            {
                sbP.AppendLine("失败：" + ex);
            }

            var outDirP = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDirP);
            File.WriteAllText(Path.Combine(outDirP, "verify_nsfw_preview.txt"), sbP.ToString());
            Exit();
            return true;
        }

        // --thumbtest <image>：验证缩略图 key/目录结构（自动化测试用）
        var ti = Array.IndexOf(cli, "--thumbtest");
        if (ti >= 0 && ti + 1 < cli.Length)
        {
            var thumbs = new ThumbnailService(AppPaths.ThumbCacheDir, 82);
            var fi = new System.IO.FileInfo(cli[ti + 1]);
            var ftUtc = fi.LastWriteTimeUtc.ToFileTimeUtc();
            long secDbg = ftUtc / 10_000_000 - 11644473600;
            double fracDbg = (ftUtc % 10_000_000) / 10_000_000.0;
            var mtime = secDbg + fracDbg;
            var built = thumbs.GetOrBuild(cli[ti + 1], mtime, fi.Length);
            var report2 = "ft = " + ftUtc + "\nsec = " + secDbg +
                          "\nfrac(R) = " + fracDbg.ToString("R") +
                          "\nsum(R) = " + mtime.ToString("R") +
                          "\nmtime = " + StoreService.Fmt(mtime) +
                          "\nF9 = " + mtime.ToString("F9", System.Globalization.CultureInfo.InvariantCulture) +
                          "\ncustom = " + mtime.ToString("0.000000000", System.Globalization.CultureInfo.InvariantCulture) +
                          "\nbits = " + BitConverter.ToString(BitConverter.GetBytes(mtime)) +
                          "\nkey = " + thumbs.Key(cli[ti + 1], mtime, fi.Length) + "\n" +
                          "cache = " + (built ?? "(null)");
            var outDir2 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(outDir2);
            File.WriteAllText(Path.Combine(outDir2, "verify_thumb.txt"), report2);
            Exit();
            return true;
        }

        // --verify-kind-whitelist：类型白名单（默认值 / 命中 / 自定义文件往返）
        if (cli.Contains("--verify-kind-whitelist"))
        {
            var lines = new List<string>();
            var cases = new (string Label, string[] Tags)[]
            {
                ("solo", ["solo"]),
                ("solo+depth", ["solo", "depth_of_field", "weapon"]),
                ("multiple", ["2girls", "multiple_girls", "solo"]),
                ("scenery", ["scenery", "no_humans"]),
                ("none-hit", ["long_hair", "smile"]),
            };
            foreach (var (label, tags) in cases)
                lines.Add($"{label,-12} -> [{string.Join(", ", Services.Taxonomy.KindTagsFrom(tags))}]");

            var custom = new Dictionary<string, string[]>
            {
                ["自定义类型"] = ["solo", "custom_tag"],
            };
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "kind_whitelist_test.json");
            Services.Taxonomy.SaveKindWhitelist(path, custom);
            var loaded = Services.Taxonomy.LoadKindWhitelist(path);
            lines.Add($"custom file -> {string.Join(", ", loaded.Keys)} / " +
                $"[{string.Join(", ", Services.Taxonomy.KindTagsFrom(["custom_tag", "solo"], loaded))}]");
            var restored = Services.Taxonomy.LoadKindWhitelist("/nonexistent/kind_whitelist.json");
            lines.Add($"missing file -> fallback groups={restored.Count}");

            var report = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "verify_kind.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report)!);
            System.IO.File.WriteAllLines(report, lines);
            Exit();
            return true;
        }

        // --verify-collage：拼图布局与合成验收（纯逻辑 + 输出像素，不出窗口）
        if (cli.Contains("--verify-collage"))
        {
            try
            {
                Task.Run(() => RunVerifyCollageAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppService.DebugLog("verify-collage failed: " + ex);
            }
            Exit();
            return true;
        }

        // --verify-drag-guard：验证「模糊保护时拒绝拖出」的判定（纯逻辑，不弹窗）
        if (cli.Contains("--verify-drag-guard"))
        {
            var lines = new List<string>();
            Gallery.Models.PhotoItem Make(string name, string? nsfw) => new()
            {
                File = name,
                FullPath = name,
                PixelWidth = 1,
                PixelHeight = 1,
                Date = DateTime.MinValue,
                Nsfw = nsfw,
            };

            var safe = Make("safe.png", "g");
            var bold = Make("bold.png", "e");
            var unknown = Make("unknown.png", null);

            foreach (var mode in new[] { "normal", "blur", "hide" })
            {
                Services.AppService.Settings.Settings.nsfw_display = mode;
                lines.Add($"mode={mode}");
                foreach (var (label, list) in new (string, List<Gallery.Models.PhotoItem>)[]
                {
                    ("safe", new() { safe }),
                    ("bold", new() { bold }),
                    ("mixed", new() { safe, bold }),
                    ("unknown", new() { unknown }),
                })
                {
                    var blocked = Gallery.Views.GalleryPage.IsBlurBlocked(list, out var message);
                    lines.Add($"  {label,-8} blocked={blocked} message={(message.Length == 0 ? "-" : message)}");
                }
            }

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "verify_drag.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllLines(path, lines);
            Exit();
            return true;
        }

        // --verify-fileops <imageId>：批次 2 验收（改名/删除/移动），写报告后退出
        var vf = Array.IndexOf(cli, "--verify-fileops");
        if (vf >= 0 && vf + 1 < cli.Length)
        {
            var rep = new StringBuilder();
            try
            {
                var store3 = new StoreService(AppPaths.DbPath);
                var id = int.Parse(cli[vf + 1]);
                var img = store3.GetImage(id) ?? throw new Exception("图片不存在");
                var beforeKey = img.FileKey;

                // ① 重命名：file_key 不变（同卷改名保身份）
                var newStem = "renamed_" + DateTime.Now.Ticks % 100000;
                var (ok, err) = FileActions.RenameImage(store3, id, newStem);
                rep.AppendLine($"rename: ok={ok} err={err}");
                var after = store3.GetImage(id);
                rep.AppendLine($"  file_key unchanged = {after?.FileKey == beforeKey}, name = {after?.Name}");

                // ② 删除：回收站 + missing=1
                var del = FileActions.DeleteImages(store3, new[] { id });
                var afterDel = store3.GetImage(id);
                rep.AppendLine($"delete: done={del.Done.Count} errors=[{string.Join("; ", del.Errors)}] missing={afterDel?.Missing}");

                // ③ 复制到 Temp（图库外，记录不动）——rename 后重查最新行
                var cur = store3.GetImage(id);
                if (cur is not null)
                {
                    var (cdone, cerr) = FileActions.CopyImages(store3, new[] { id }, System.IO.Path.GetTempPath());
                    rep.AppendLine($"copy: done={cdone.Count} errors=[{string.Join("; ", cerr)}]");
                }
            }
            catch (Exception ex) { rep.AppendLine("ERROR: " + ex.Message); }
            var od = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
            Directory.CreateDirectory(od);
            File.WriteAllText(Path.Combine(od, "verify_fileops.txt"), rep.ToString());
            Exit();
            return true;
        }

        // --verify-import：本地文件夹导入待审核队列的沙箱验收（枚举 / 去重 / 保留原文件）
        if (cli.Contains("--verify-import"))
        {
            RunVerifyImport();
            Exit();
            return true;
        }

        // --verify-archive：阶段 D 归档流水线验收（沙箱内造文件→归档→校验，写报告后退出）
        if (cli.Contains("--verify-archive"))
        {
            RunVerifyArchive();
            Exit();
            return true;
        }

        // --relocate-root <旧根> <新根> [--force]：图库根换位置（文件须已在磁盘就位），只改库与设置里的路径前缀
        var relocate = Array.IndexOf(cli, "--relocate-root");
        if (relocate >= 0 && relocate + 2 < cli.Length)
        {
            RunRelocate(cli[relocate + 1], cli[relocate + 2], cli.Contains("--force"));
            Exit();
            return true;
        }

        // --verify-relocate：沙箱验收（造库 → 搬文件 → 改路径 → 断言），写报告后退出
        if (cli.Contains("--verify-relocate"))
        {
            RunVerifyRelocate();
            Exit();
            return true;
        }

        // --tag-repair [--apply]：按 tag_identity.json 修身份/状态/显示，默认只预演
        if (cli.Contains("--tag-repair"))
        {
            RunTagRepair(cli.Contains("--apply"));
            Exit();
            return true;
        }

        // --merge-tags <plan.json> [--apply]：合并重复标签（作品/画师），默认只预演
        var mergeTags = Array.IndexOf(cli, "--merge-tags");
        if (mergeTags >= 0 && mergeTags + 1 < cli.Length)
        {
            RunMergeTags(cli[mergeTags + 1], cli.Contains("--apply"));
            Exit();
            return true;
        }

        // --verify-merge-tags：沙箱验收（造标签 → 合并 → 断言），写报告后退出
        if (cli.Contains("--verify-merge-tags"))
        {
            RunVerifyMergeTags();
            Exit();
            return true;
        }

        // --dedupe-rows [--apply]：合并「同一路径多行」的重复记录，默认只预演
        if (cli.Contains("--dedupe-rows"))
        {
            RunDedupeRows(cli.Contains("--apply"));
            Exit();
            return true;
        }

        // --verify-dedupe-rows：沙箱验收（造重复行 → 合并 → 断言），写报告后退出
        if (cli.Contains("--verify-dedupe-rows"))
        {
            RunVerifyDedupeRows();
            Exit();
            return true;
        }

        // --organize [--apply]：按标签把受管目录整理成 作品/画师 两级，默认只预演
        if (cli.Contains("--organize"))
        {
            RunOrganize(cli.Contains("--apply"));
            Exit();
            return true;
        }

        // --verify-organize：沙箱验收（造目录与标签 → 整理 → 断言），写报告后退出
        if (cli.Contains("--verify-organize"))
        {
            RunVerifyOrganize();
            Exit();
            return true;
        }

        // --verify-foldermap：v5 逐级目录模板验收（纯逻辑，写报告后退出）
        if (cli.Contains("--verify-foldermap"))
        {
            RunVerifyFolderMap();
            Exit();
            return true;
        }

        // --addroot <path>：非交互添加根目录 + 扫描（自动化测试用）
        var idx = Array.IndexOf(cli, "--addroot");
        if (idx >= 0 && idx + 1 < cli.Length)
        {
            RunAddRoot(cli[idx + 1]);
            Exit();
            return true;
        }

        return false;
    }
    private static void RunVerification()
    {
        var report = new StringBuilder();
        try
        {
            SettingsStore settings = new();
            settings.Load();
            report.AppendLine($"settings.roots = {settings.Settings.roots.Count}");
            var store = new StoreService(AppPaths.DbPath);
            report.AppendLine($"schema_version = {store.SchemaVersion}");
            var roots = store.Roots();
            report.AppendLine($"roots = {roots.Count} (enabled {roots.Count(r => r.Enabled)})");
            foreach (var root in roots) report.AppendLine($"  root[{root.Id}] {root.Path} enabled={root.Enabled}");
            report.AppendLine($"images(missing=0) = {store.CountImages()}");
            report.AppendLine($"favorites = {store.CountFavorites()}");

            // 自然排序抽样（中英数字混合）
            var names = new[] { "a2.jpg", "a10.jpg", "ab.jpg", "图2.png", "图10.png", "B.jpg", "#1.jpg", "z3.jpg" };
            var sorted = names.OrderBy(n => n, Comparer<string>.Create((x, y) => NaturalSort.CompareNames(x, y)));
            report.AppendLine("natural_sort = " + string.Join(" | ", sorted));

            // 逐根目录计数（与 galleryold 口径对照）
            foreach (var root in roots)
            {
                var folders = store.Folders(root.Id);
                var total = folders.Sum(f => f.Count);
                report.AppendLine($"  root[{root.Id}] dirs={folders.Count} images={total}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "verify_db.txt"), report.ToString());
    }

    /// <summary>
    /// 视频接入验收：只在 --dataroot 沙箱里跑，扫 <c>video-src</c> 下的 mp4，
    /// 检查入库字段、媒体筛选、重复扫描幂等与封面缓存。播放仍需人工验收。
    /// </summary>
    private static void RunVerifyVideo()
    {
        var report = new StringBuilder();
        try
        {
            if (AppPaths.Sandbox is null)
            {
                report.AppendLine("ERROR: --verify-video 必须在 --dataroot 沙箱内运行");
            }
            else
            {
                var settings = new SettingsStore();
                settings.Load();
                var store = new StoreService(AppPaths.DbPath);
                report.AppendLine($"schema_version = {store.SchemaVersion}（期望 5）");
                var sample = store.QueryOne("SELECT * FROM images LIMIT 1");
                report.AppendLine(sample is null
                    ? "images = 空库"
                    : $"columns ok: media_kind={sample.MediaKind} duration={sample.Duration}");

                var src = Path.Combine(AppPaths.Sandbox, "video-src");
                Directory.CreateDirectory(src);
                var mp4Count = Directory.EnumerateFiles(src, "*.mp4", SearchOption.AllDirectories).Count();
                report.AppendLine($"video-src mp4 = {mp4Count}");

                var folders = new FolderService(store, settings);
                var (root, scan) = folders.AddRootAndScan(src);
                report.AppendLine($"scan#1 = {scan.Summary()}");
                report.AppendLine($"  added={scan.Added} updated={scan.Updated} errors={scan.Errors.Count}");
                var again = new ScannerService(store, settings.Settings).ScanRoot(root.Id);
                report.AppendLine($"scan#2 幂等 = {again.Summary()}");
                report.AppendLine($"  added={again.Added} updated={again.Updated}");

                var videos = store.Query(new StoreService.QuerySpec { MediaKind = "video", Sort = "name" });
                var images = store.Query(new StoreService.QuerySpec { MediaKind = "image", Sort = "name" });
                report.AppendLine($"videos = {videos.Count}；images = {images.Count}");
                var video = videos.FirstOrDefault();
                if (video is not null)
                {
                    report.AppendLine($"  {video.Name}: kind={video.MediaKind} duration={video.Duration:0.###} " +
                                      $"size={video.Width}x{video.Height}");
                    var cover = new ScannerService(store, settings.Settings).Thumbs.GetOrBuildVideo(
                        video.AbsPath, video.Mtime, video.Size, ThumbnailService.CacheEdge);
                    report.AppendLine($"  cover = {(cover is null ? "未生成（显示占位）" : cover)}");
                    report.AppendLine($"  cover exists = {cover is not null && File.Exists(cover)}");
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "verify_video.txt"), report.ToString());
    }

    /// <summary>
    /// 资料包导入验收：只在 --dataroot 沙箱里跑，把给定 zip／文件夹导入后检查落盘位置、
    /// 条目列表与词库导入结果。只覆盖数据链路；拖拽手感与提示框仍需人工验收。
    /// </summary>
    private static void RunVerifyPacks(string source)
    {
        var report = new StringBuilder();
        try
        {
            if (AppPaths.Sandbox is null)
            {
                report.AppendLine("ERROR: --verify-packs 必须在 --dataroot 沙箱内运行");
            }
            else
            {
                AppPaths.EnsureDirs();
                report.AppendLine($"source = {source}");
                report.AppendLine($"pack root = {PackFolders.Root}");
                report.AppendLine($"archive = {PackFolders.Archive}");
                report.AppendLine($"models = {PackFolders.Models}");

                Windows.Storage.IStorageItem item = Directory.Exists(source)
                    ? Windows.Storage.StorageFolder.GetFolderFromPathAsync(source).AsTask().GetAwaiter().GetResult()
                    : Windows.Storage.StorageFile.GetFileFromPathAsync(source).AsTask().GetAwaiter().GetResult();

                var result = PackLibrary.ImportAsync(new[] { item }).GetAwaiter().GetResult();
                report.AppendLine($"imported: catalog={result.Catalog} model={result.Model}");
                foreach (var error in result.Errors) report.AppendLine("  error: " + error);

                var entries = PackLibrary.List();
                report.AppendLine($"entries = {entries.Count}");
                foreach (var entry in entries)
                {
                    var exists = Directory.Exists(entry.Path) ? "ok" : "missing";
                    report.AppendLine($"  [{(entry.IsModel ? "model" : "catalog")}] {entry.Name} · {entry.Detail} · {exists}");
                }

                var store = new StoreService(AppPaths.DbPath);
                var catalog = new TagCatalogService(store);
                var reports = catalog.ImportAll(log: line => report.AppendLine("  " + line));
                report.AppendLine($"catalog imports = {reports.Count}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "verify_packs.txt"), report.ToString());
    }

    /// <summary>
    /// 本地导入验收：枚举（含/不含子目录）、重复跳过、归档时复制保留原文件或移动。
    /// </summary>
    private static void RunVerifyImport()
    {
        var report = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "ptv", "import");
        var libRoot = Path.Combine(sandbox, "lib");
        var source = Path.Combine(sandbox, "source");
        try
        {
            SettingsStore settings = new();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var archiver = new ArchiveService(store);

            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.CreateDirectory(libRoot);
            Directory.CreateDirectory(Path.Combine(source, "子目录"));

            var pixel = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            var a = Path.Combine(source, "a.png");
            var b = Path.Combine(source, "b.png");
            var nested = Path.Combine(source, "子目录", "c.png");
            File.WriteAllBytes(a, pixel);
            File.WriteAllBytes(b, pixel);
            File.WriteAllBytes(nested, pixel);
            File.WriteAllText(Path.Combine(source, "note.txt"), "not an image");

            report.AppendLine($"sandbox = {sandbox}");
            var (root, scan) = folders.AddRootAndScan(libRoot);
            report.AppendLine($"root[{root.Id}] scan={scan.Summary()}");

            var flat = LocalImageImport.Enumerate(source, recursive: false);
            report.AppendLine($"非递归枚举 = {flat.Count}（期望 2）");

            var all = LocalImageImport.Enumerate(source, recursive: true);
            report.AppendLine($"递归枚举 = {all.Count}（期望 3）");

            var first = store.EnqueueLocalImages(flat, keepSource: true);
            report.AppendLine($"首次导入 = added {first.Added} / skipped {first.Skipped}（期望 2 / 0）");

            var again = store.EnqueueLocalImages(flat, keepSource: true);
            report.AppendLine($"重复导入 = added {again.Added} / skipped {again.Skipped}（期望 0 / 2）");

            var deep = store.EnqueueLocalImages([nested], keepSource: true);
            report.AppendLine($"子目录导入 = added {deep.Added} / skipped {deep.Skipped}（期望 1 / 0）");
            report.AppendLine($"待归档条数 = {store.PendingIngestCount()}（期望 3）");

            var rows = store.IngestRows("pending", int.MaxValue).OrderBy(r => r.Id).ToList();
            foreach (var row in rows)
                report.AppendLine($"  #{row.Id} {row.InboxName} path={IngestReview.LocalPath(row)}");

            var copyTarget = Path.Combine(libRoot, "保留");
            var copy = archiver.Archive(rows[0].Id, copyTarget);
            report.AppendLine($"复制归档 → ok={copy.Ok} msg={copy.Message}");
            report.AppendLine($"  源仍存在 = {File.Exists(a)}（期望 True）");
            report.AppendLine($"  目标存在 = {File.Exists(copy.Destination)}（期望 True）");
            report.AppendLine($"  作业阶段 = {store.ArchiveJob(copy.Destination)?.Phase}（期望 copied）");

            var moveTarget = Path.Combine(libRoot, "移动");
            var moveSource = Path.Combine(source, "d.png");
            File.WriteAllBytes(moveSource, pixel);
            var moving = store.EnqueueLocalImages([moveSource], keepSource: false);
            var moveRow = store.IngestRows("pending", int.MaxValue).OrderBy(r => r.Id).Last();
            var move = archiver.Archive(moveRow.Id, moveTarget);
            report.AppendLine($"移动导入 = added {moving.Added}（期望 1）");
            report.AppendLine($"移动归档 → ok={move.Ok} msg={move.Message}");
            report.AppendLine($"  源已删除 = {!File.Exists(moveSource)}（期望 True）");
            report.AppendLine($"  作业阶段 = {store.ArchiveJob(move.Destination)?.Phase}（期望 moved）");
            report.AppendLine($"图库图片数 = {store.CountImages()}（期望 2）");
            report.AppendLine($"剩余待归档 = {store.PendingIngestCount()}（期望 2：b、c）");
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? "");
        }

        WriteVerifyReport("verify_import.txt", report);
    }

    /// <summary>
    /// 阶段 D 归档验收：在沙箱里造一个 inbox 文件与一条 ingest 记录，
    /// 归档到根目录下的子目录，校验「源已删 / 目标在 / 状态 done / 作业落库 / images 有记录」。
    /// </summary>
    private static void RunVerifyArchive()
    {
        var report = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "ptv", "archive");
        var libRoot = Path.Combine(sandbox, "lib");
        var inboxRoot = Path.Combine(sandbox, "inbox");
        try
        {
            SettingsStore settings = new();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var scanner = new ScannerService(store, settings.Settings);
            var folders = new FolderService(store, settings);
            var archiver = new ArchiveService(store);

            Directory.CreateDirectory(libRoot);
            Directory.CreateDirectory(inboxRoot);
            Directory.CreateDirectory(Path.Combine(libRoot, "分类A"));

            report.AppendLine($"sandbox = {sandbox}");
            report.AppendLine($"dataroot db = {AppPaths.DbPath}");

            var roots = store.Roots();
            var root = roots.FirstOrDefault(r => string.Equals(r.Path, libRoot, StringComparison.OrdinalIgnoreCase));
            if (root is null)
            {
                var (added, scan) = folders.AddRootAndScan(libRoot);
                root = added;
                report.AppendLine($"add root[{added.Id}] scan={scan.Summary()}");
            }
            else
            {
                report.AppendLine($"reuse root[{root.Id}] {root.Path}");
            }

            // 造一个 1x1 PNG 当作下载完成的文件
            var source = Path.Combine(inboxRoot, "verify_archive_pixel.png");
            File.WriteAllBytes(source, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
            var bytes = new FileInfo(source).Length;

            var payload = "{\"site\":\"danbooru\",\"source\":\"danbooru\"," +
                          "\"page_url\":\"https://danbooru.donmai.us/posts/999001\",\"rating\":\"e\"," +
                          "\"tags\":{\"artist\":[\"verify_artist\"],\"copyright\":[\"verify_work\"]," +
                          "\"character\":[],\"general\":[\"verify_general\"],\"meta\":[]}}";
            var ingest = store.IngestPush(payload, Path.GetFileName(source), source,
                "https://danbooru.donmai.us/posts/999001", "", "999001", "",
                "g", "danbooru", "verify-archive-1");
            report.AppendLine($"ingest queued = #{ingest.QueueId} duplicate={ingest.Duplicate} size={bytes}");

            // 1) 目标不在图库根里 → 应当拒绝
            var outside = archiver.Archive(ingest.QueueId, Path.Combine(sandbox, "outside"));
            report.AppendLine($"outside → ok={outside.Ok} msg={outside.Message}");

            // 2) 源文件不存在 → 应当拒绝
            File.Move(source, source + ".tmp");
            var missing = archiver.Archive(ingest.QueueId, Path.Combine(libRoot, "分类A"));
            report.AppendLine($"missing source → ok={missing.Ok} msg={missing.Message}");
            File.Move(source + ".tmp", source);

            // 3) 正常归档
            var destination = Path.Combine(libRoot, "分类A");
            var result = archiver.Archive(ingest.QueueId, destination);
            report.AppendLine($"archive → ok={result.Ok} msg={result.Message}");
            report.AppendLine($"  dest = {result.Destination}");
            report.AppendLine($"  source exists = {File.Exists(source)}");
            report.AppendLine($"  dest exists = {File.Exists(result.Destination)} size={SafeLength(result.Destination)}");

            var row = store.IngestRow(ingest.QueueId);
            report.AppendLine($"  ingest state = {row?.State} note={row?.InboxPath}");
            var job = store.ArchiveJob(result.Destination);
            report.AppendLine($"  archive_jobs = {(job is null ? "（没有）" : $"phase={job.Value.Phase} error='{job.Value.Error}'")}");

            var rel = Path.GetRelativePath(root.Path, result.Destination).Replace('\\', '/');
            var image = store.QueryOne("SELECT * FROM images WHERE root_id=$r AND rel_path=$p",
                ("$r", root.Id), ("$p", rel));
            report.AppendLine($"  images row = {(image is null ? "（没有）" : $"id={image.Id} dir={image.DirRel} size={image.Size}")}");

            if (image is not null)
            {
                var tags = store.CategorizedTags(image.Id);
                report.AppendLine("  tags = " + string.Join(" | ", tags.Select(t => $"{t.Category}/{t.Name}")));

                var score = store.NsfwScoreFor(image.Id);
                report.AppendLine($"  nsfw = {image.Nsfw ?? "(空)"} · score = " +
                    (score is null ? "（没有）" : $"source={score.Source} rating={score.Rating}"));
                report.AppendLine("  blur 模式可见 = " + store.Query(
                    new StoreService.QuerySpec { HideNsfw = true }).Count);
            }

            // 4) 同一队列项重复归档 → 应拒绝（状态已 done）
            var again = archiver.Archive(ingest.QueueId, destination);
            report.AppendLine($"re-archive → ok={again.Ok} msg={again.Message}");

            // 5) 同名文件再归档一次 → 应得到带后缀的新文件，不覆盖旧的
            var source2 = Path.Combine(inboxRoot, "verify_archive_pixel.png");
            File.WriteAllBytes(source2, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
            var ingest2 = store.IngestPush(payload.Replace("999001", "999002"), Path.GetFileName(source2), source2,
                "https://danbooru.donmai.us/posts/999002", "", "999002", "", "g", "danbooru", "verify-archive-2");
            var second = archiver.Archive(ingest2.QueueId, destination);
            report.AppendLine($"same-name → ok={second.Ok} dest={Path.GetFileName(second.Destination)} msg={second.Message}");
            report.AppendLine($"  both exist = {File.Exists(result.Destination)} / {File.Exists(second.Destination)}");

            report.AppendLine($"  pending now = {store.PendingIngestCount()}");
            foreach (var note in store.TrackingNotes) report.AppendLine("note: " + note);
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? "");
        }

        WriteVerifyReport("verify_archive.txt", report);
    }

    /// <summary>
    /// 图库根换位置（跨盘搬家）：文件已经在磁盘上就位，这里把库与设置里的路径前缀改掉。
    /// 保留 image id，所以标签 / NSFW 判定 / 收藏 / 来源全部不动。
    /// 报告写到 %TEMP%\ptv\relocate.txt。
    /// </summary>
    private static void RunRelocate(string oldRoot, string newRoot, bool force)
    {
        var report = new StringBuilder();
        var scope = AppPaths.Sandbox ?? AppPaths.LocalDataDir;
        if (!SingleInstance.TryAcquire(scope))
        {
            report.AppendLine("有另一个 Gallery 实例正在运行，请先退出再搬（重试一次即可）。");
        }
        else
        {
            try
            {
                SettingsStore settings = new();
                settings.Load();
                var store = new StoreService(AppPaths.DbPath);
                var audit = RelocateService.Inspect(store, oldRoot, newRoot);
                report.AppendLine("== 体检 ==");
                report.AppendLine(audit.Describe());
                if (!audit.Ok && !force)
                {
                    report.AppendLine("体检未通过，未做任何改动。");
                }
                else
                {
                    report.AppendLine("== 执行 ==");
                    report.AppendLine(RelocateService.Apply(store, settings, audit, force));
                }
                store.Dispose();
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine(ex.StackTrace ?? "");
            }
        }
        WriteVerifyReport("relocate.txt", report);
    }

    /// <summary>
    /// 沙箱验收：造一个小图库 → 模拟搬文件（其中一个故意不搬）→ 改根路径 → 断言
    /// （体检拦截、roots.path、abs_path、missing 归属、标签关联数、sidecar 前缀、settings）。
    /// </summary>
    private static void RunVerifyRelocate()
    {
        var report = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "ptv", "relocate");
        var oldLib = Path.Combine(sandbox, "lib-old");
        var newLib = Path.Combine(sandbox, "lib-new");
        var pass = 0;
        var fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++;
            else fail++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        try
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            AppPaths.ApplySandbox(sandbox);
            AppPaths.EnsureDirs();
            Directory.CreateDirectory(oldLib);
            Directory.CreateDirectory(Path.Combine(oldLib, "cartoon"));
            Directory.CreateDirectory(newLib);

            var pixel = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            foreach (var name in new[] { "a.png", "b.png", "c.png", "d.png" })
                File.WriteAllBytes(Path.Combine(oldLib, "cartoon", name), pixel);

            var settings = new SettingsStore();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var (root, scan) = folders.AddRootAndScan(oldLib);
            report.AppendLine($"sandbox = {sandbox}");
            report.AppendLine($"旧根    = [{root.Id}] {root.Path}  scan={scan.Summary()}");

            var rows = store.QueryAll("SELECT * FROM images WHERE root_id=$r", ("$r", root.Id));
            var ids = rows.Select(r => r.Id).ToList();
            store.AddTags(ids, ["wlop", "zenless_zone_zero", "单人"]);
            store.SetCategorizedTags(ids, [("artist", "wlop"), ("work", "zenless_zone_zero"), ("kind", "单人")]);
            store.Commit();
            store.WriteSidecar(root.Id, "cartoon");
            // d.png：模拟"库里记着 missing、但文件其实在"的历史漂移行（真实库里就有 132 组这种大小写重复行）
            store.Exec($"UPDATE images SET missing=1 WHERE root_id={root.Id} AND rel_path='cartoon/d.png'");
            var linksBefore = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");
            var sidecarBefore = RawLong(AppPaths.DbPath,
                $"SELECT COUNT(*) FROM sidecar_state WHERE substr(path,1,{oldLib.Length}) = '{Sql(oldLib)}' COLLATE NOCASE");
            report.AppendLine($"造数据  = {rows.Count} 张 / 标签关联 {linksBefore} / sidecar 记录 {sidecarBefore}");

            // 模拟磁盘搬运：a、b、d 搬到新根；c 故意漏掉；sidecar 跟着走
            Directory.CreateDirectory(Path.Combine(newLib, "cartoon"));
            File.Move(Path.Combine(oldLib, "cartoon", "a.png"), Path.Combine(newLib, "cartoon", "a.png"));
            File.Move(Path.Combine(oldLib, "cartoon", "b.png"), Path.Combine(newLib, "cartoon", "b.png"));
            File.Move(Path.Combine(oldLib, "cartoon", "d.png"), Path.Combine(newLib, "cartoon", "d.png"));
            File.Delete(Path.Combine(oldLib, "cartoon", "c.png"));
            var sidecarPath = Path.Combine(oldLib, "cartoon", StoreService.SidecarName);
            if (File.Exists(sidecarPath))
                File.Move(sidecarPath, Path.Combine(newLib, "cartoon", StoreService.SidecarName));

            var audit = RelocateService.Inspect(store, oldLib, newLib);
            report.AppendLine("== 体检 ==");
            report.Append(audit.Describe());
            Check("缺文件时体检不通过", !audit.Ok, $"problems={audit.Problems.Count}");
            Check("体检认出 2 个文件已到位", audit.OnDisk == 2, $"onDisk={audit.OnDisk}");

            report.AppendLine("== 执行（force）==");
            report.AppendLine(RelocateService.Apply(store, settings, audit, force: true));
            store.Dispose();

            var newRootPath = RawText(AppPaths.DbPath, $"SELECT path FROM roots WHERE id={root.Id}");
            Check("roots.path 已改到新根",
                FileIdentity.PathKey(newRootPath) == FileIdentity.PathKey(newLib), newRootPath);

            var strayAbs = RawLong(AppPaths.DbPath,
                $"SELECT COUNT(*) FROM images WHERE root_id={root.Id} AND substr(abs_path,1,{newLib.Length}) <> '{Sql(newLib)}' COLLATE NOCASE");
            Check("abs_path 全部指向新根", strayAbs == 0, $"不符 {strayAbs} 行");

            var stillThere = RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM images WHERE root_id={root.Id} AND missing=0");
            var nowMissing = RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM images WHERE root_id={root.Id} AND missing=1");
            Check("跟过来的 2 张 missing=0", stillThere == 2, $"missing=0 共 {stillThere}");
            Check("没跟过来的 1 张 + 历史 missing 1 张 = missing=1 共 2 张", nowMissing == 2, $"missing=1 共 {nowMissing}");
            var dryMissing = RawText(AppPaths.DbPath,
                $"SELECT missing FROM images WHERE root_id={root.Id} AND rel_path='cartoon/d.png'");
            Check("历史 missing 行不被复活（d.png 仍 missing=1）", dryMissing == "1", $"missing={dryMissing}");

            var linksAfter = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");
            Check("标签关联数不变", linksAfter == linksBefore, $"{linksBefore} → {linksAfter}");

            var sidecarOld = RawLong(AppPaths.DbPath,
                $"SELECT COUNT(*) FROM sidecar_state WHERE substr(path,1,{oldLib.Length}) = '{Sql(oldLib)}' COLLATE NOCASE");
            var sidecarNew = RawLong(AppPaths.DbPath,
                $"SELECT COUNT(*) FROM sidecar_state WHERE substr(path,1,{newLib.Length}) = '{Sql(newLib)}' COLLATE NOCASE");
            Check("sidecar 记录前缀已改", sidecarOld == 0 && sidecarNew == sidecarBefore,
                $"旧 {sidecarOld} / 新 {sidecarNew}");

            var reopened = new SettingsStore();
            reopened.Load();
            Check("settings.roots 已换成新根",
                reopened.Settings.roots.Any(p => FileIdentity.PathKey(p) == FileIdentity.PathKey(newLib))
                && !reopened.Settings.roots.Any(p => FileIdentity.PathKey(p) == FileIdentity.PathKey(oldLib)),
                string.Join(" | ", reopened.Settings.roots));

            // 改完再体检一次：不应再有问题（没跟过来的那张已按 missing 记账）
            var store2 = new StoreService(AppPaths.DbPath);
            var audit2 = RelocateService.Inspect(store2, newLib, newLib);
            Check("改完再体检通过", audit2.Ok, $"problems={audit2.Problems.Count} missing={audit2.MissingRows}");
            store2.Dispose();
        }
        catch (Exception ex)
        {
            fail++;
            report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? "");
        }

        report.AppendLine($"结果: PASS {pass} / FAIL {fail}");
        WriteVerifyReport("verify_relocate.txt", report);
    }

    /// <summary>
    /// 按 assets/tag_catalog/tag_identity.json 修标签身份（--tag-repair [--apply]）。
    /// 默认只预演；执行前自动备份库。报告写到 %TEMP%\ptv\tag_repair.txt。
    /// </summary>
    private static void RunTagRepair(bool apply)
    {
        var report = new StringBuilder();
        var scope = AppPaths.Sandbox ?? AppPaths.LocalDataDir;
        if (!SingleInstance.TryAcquire(scope))
        {
            report.AppendLine("有另一个 Gallery 实例正在运行，请先退出再修标签（重试一次即可）。");
        }
        else
        {
            try
            {
                var store = new StoreService(AppPaths.DbPath);
                var catalog = new TagCatalogService(store);
                if (apply) report.AppendLine("备份：" + FolderService.BackupBeforeChange());
                var result = catalog.AlignTags(dryRun: !apply, log: line => report.AppendLine(line));
                report.AppendLine();
                report.AppendLine($"结果：新关联 {result.Matched} · 已有 {result.Existing} · " +
                                  $"同名多实体 {result.Ambiguous} · 作品冲突 {result.Conflicting} · 无对应 {result.Unmatched} · " +
                                  $"外部/原创 {result.External} · 人工 {result.Manual} · " +
                                  $"跳过(非角色/0图) {result.Skipped}");
                report.AppendLine($"计划状态写入 {result.Seeded} · 身份映射 {result.IdentityLinks} · " +
                                  $"合并实体 {result.MergedEntities} · 合并标签 {result.MergedTags}");
                var ambiguous = catalog.AmbiguousTags(6, 50);
                report.AppendLine($"待人工消歧：{ambiguous.Count} 条" +
                                  (ambiguous.Count == 0 ? "" :
                                   " · " + string.Join("、", ambiguous.Take(10).Select(t => t.TagName))));
                report.AppendLine(apply ? "已执行。" : "预演结束，未改任何数据。");
                store.Dispose();
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine(ex.StackTrace ?? "");
            }
        }
        WriteVerifyReport("tag_repair.txt", report);
    }

    /// <summary>
    /// 合并重复标签（--merge-tags plan.json [--apply]）。默认只预演；执行前自动备份库。
    /// 报告写到 %TEMP%\ptv\merge_tags.txt。
    /// </summary>
    private static void RunMergeTags(string planPath, bool apply)
    {
        var report = new StringBuilder();
        var scope = AppPaths.Sandbox ?? AppPaths.LocalDataDir;
        if (!SingleInstance.TryAcquire(scope))
        {
            report.AppendLine("有另一个 Gallery 实例正在运行，请先退出再合并（重试一次即可）。");
        }
        else
        {
            try
            {
                var store = new StoreService(AppPaths.DbPath);
                report.AppendLine(TagMergeService.Run(store, planPath, apply));
                store.Dispose();
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine(ex.StackTrace ?? "");
            }
        }
        WriteVerifyReport("merge_tags.txt", report);
    }

    /// <summary>
    /// 沙箱验收：造 3 张图与重复标签 → 合并 → 断言
    /// （图片转移、重复关联去重、别名与中文名、loser 删除、sidecar 重写、EnsureTag 别名重定向、重复执行中止）。
    /// </summary>
    private static void RunVerifyMergeTags()
    {
        var report = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "ptv", "mergetags");
        var lib = Path.Combine(sandbox, "lib");
        var pass = 0;
        var fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++;
            else fail++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        try
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            AppPaths.ApplySandbox(sandbox);
            AppPaths.EnsureDirs();
            Directory.CreateDirectory(Path.Combine(lib, "cartoon"));

            var pixel = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            foreach (var name in new[] { "a.png", "b.png", "c.png" })
                File.WriteAllBytes(Path.Combine(lib, "cartoon", name), pixel);

            var settings = new SettingsStore();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var (root, scan) = folders.AddRootAndScan(lib);
            report.AppendLine($"sandbox = {sandbox}");
            report.AppendLine($"图库    = [{root.Id}] {root.Path}  scan={scan.Summary()}");

            var rows = store.QueryAll("SELECT * FROM images WHERE root_id=$r ORDER BY name", ("$r", root.Id));
            var a = rows[0].Id;
            var b = rows[1].Id;
            var c = rows[2].Id;

            var winnerArtist = store.EnsureTag("ui_uiiiiiiiii", "artist");
            store.SetCategorizedTags([a], [("work", "honkai"), ("work", "honkai:_star_rail"), ("artist", "uiiiiiiiii")]);
            store.SetCategorizedTags([b], [("work", "honkai")]);
            store.SetCategorizedTags([c], [("work", "崩坏星穹铁道")]);
            store.WriteSidecar(root.Id, "cartoon");
            var winnerWork = store.EnsureTag("honkai:_star_rail", "work");
            var linksBefore = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");
            var tagsBefore = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM tags");

            var planPath = Path.Combine(sandbox, "plan.json");
            File.WriteAllText(planPath, """
                [
                  {"loser":["work","honkai"],"winner":["work","honkai:_star_rail"],"zh":"崩坏星穹铁道"},
                  {"loser":["work","崩坏星穹铁道"],"winner":["work","honkai:_star_rail"]},
                  {"loser":["artist","uiiiiiiiii"],"winner":["artist","ui_uiiiiiiiii"]}
                ]
                """);

            var dry = TagMergeService.Run(store, planPath, apply: false);
            var afterDry = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");
            Check("预演不写数据", afterDry == linksBefore, $"image_tags {linksBefore} → {afterDry}");

            report.AppendLine("---- 执行 ----");
            report.AppendLine(TagMergeService.Run(store, planPath, apply: true));
            // 合并刚结束就取快照：后面的 EnsureTag 断言自己会新建标签，别把计数弄脏
            var linksAfter = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");
            var tagsAfterMerge = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM tags");

            // 1) 标签行
            Check("loser 标签行已删除",
                RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM tags WHERE category='work' AND name IN ('honkai','崩坏星穹铁道')") == 0
                && RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM tags WHERE category='artist' AND name='uiiiiiiiii'") == 0);

            // 2) 图片归属
            string WorkOf(int id) => RawText(AppPaths.DbPath,
                $"SELECT name FROM tags t JOIN image_tags it ON it.tag_id=t.id WHERE it.image_id={id} AND t.category='work'");
            string ArtistOf(int id) => RawText(AppPaths.DbPath,
                $"SELECT name FROM tags t JOIN image_tags it ON it.tag_id=t.id WHERE it.image_id={id} AND t.category='artist'");
            Check("a：work 去重后只剩 winner", WorkOf(a) == "honkai:_star_rail", WorkOf(a));
            Check("b：loser work 转到 winner", WorkOf(b) == "honkai:_star_rail", WorkOf(b));
            Check("c：中文 loser 转到 winner", WorkOf(c) == "honkai:_star_rail", WorkOf(c));
            Check("a：artist 转到 ui_uiiiiiiiii", ArtistOf(a) == "ui_uiiiiiiiii", ArtistOf(a));

            // 3) 别名 / 中文名
            var aliasHits = RawLong(AppPaths.DbPath,
                $"SELECT COUNT(*) FROM tag_aliases WHERE tag_id={winnerWork} AND alias IN ('honkai','崩坏星穹铁道')");
            Check("loser 旧名成为 winner 的别名", aliasHits == 2, $"命中 {aliasHits}");
            Check("winner 中文名已写入", RawText(AppPaths.DbPath,
                $"SELECT zh FROM tag_profiles WHERE tag_id={winnerWork}") == "崩坏星穹铁道");

            // 4) 别名重定向（新下载/重导 sidecar 时不再分裂）
            Check("EnsureTag('honkai','work') 重定向到 winner", store.EnsureTag("honkai", "work") == winnerWork);
            Check("EnsureTag 不跨分类重定向", store.EnsureTag("honkai", "custom") != winnerWork);

            // 5) sidecar 跟着重写
            var sidecar = Path.Combine(lib, "cartoon", StoreService.SidecarName);
            var workTags = new List<string>();
            if (File.Exists(sidecar))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecar));
                var fileA = doc.RootElement.GetProperty("files").GetProperty("a.png");
                if (fileA.TryGetProperty("classified_tags", out var arr))
                    foreach (var t in arr.EnumerateArray())
                        if (t.GetProperty("category").GetString() == "work")
                            workTags.Add(t.GetProperty("name").GetString() ?? "");
            }
            Check("sidecar 里 a.png 的 work 只剩 winner",
                workTags.Count == 1 && workTags[0] == "honkai:_star_rail",
                string.Join(" | ", workTags));

            // 6) 对账
            Check("image_tags 只少了 1 行重复关联", linksAfter == linksBefore - 1, $"{linksBefore} → {linksAfter}");
            Check("标签总数只少了 3 个 loser", tagsAfterMerge == tagsBefore - 3,
                $"合并前 {tagsBefore} → 现在 {tagsAfterMerge}");

            // 7) 重复执行：整体中止，不改数据
            var again = TagMergeService.Run(store, planPath, apply: true);
            Check("重复执行整体中止", again.Contains("未做任何改动"), "");
            Check("重复执行不改数据",
                RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags") == linksAfter);

            store.Dispose();
        }
        catch (Exception ex)
        {
            fail++;
            report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? "");
        }

        report.AppendLine($"结果: PASS {pass} / FAIL {fail}");
        WriteVerifyReport("verify_merge_tags.txt", report);
    }

    private static string Sql(string s) => s.Replace("'", "''");

    private static long RawLong(string dbPath, string sql)
        => long.TryParse(RawText(dbPath, sql), out var n) ? n : 0L;

    private static string RawText(string dbPath, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? "" : Convert.ToString(value) ?? "";
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : -1; }
        catch (IOException) { return -1; }
    }

    private static void WriteVerifyReport(string fileName, StringBuilder report)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "ptv");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, fileName);
        File.WriteAllText(outPath, report.ToString());
        System.Diagnostics.Debug.WriteLine(outPath);
    }

    private static void RunAddRoot(string path)
    {
        var report = new StringBuilder();
        try
        {
            SettingsStore settings = new();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var before = store.CountImages();
            var (root, scan) = folders.AddRootAndScan(path);
            report.AppendLine($"added root[{root.Id}] {root.Path}");
            report.AppendLine($"scan = {scan.Summary()}");
            report.AppendLine($"images before/after = {before}/{store.CountImages()}");
            foreach (var note in store.TrackingNotes) report.AppendLine("note: " + note);
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex.Message);
        }
        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "ptv");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "verify_addroot.txt"), report.ToString());
    }

    /// <summary>
    /// 合并重复行（--dedupe-rows [--apply]）。默认只预演；执行前自动备份库。
    /// 报告写到 %TEMP%\ptv\dedupe_rows.txt。
    /// </summary>
    private static void RunDedupeRows(bool apply)
    {
        var report = new StringBuilder();
        var scope = AppPaths.Sandbox ?? AppPaths.LocalDataDir;
        if (!SingleInstance.TryAcquire(scope))
        {
            report.AppendLine("有另一个 Gallery 实例正在运行，请先退出再合并（重试一次即可）。");
        }
        else
        {
            try
            {
                var store = new StoreService(AppPaths.DbPath);
                report.AppendLine(RowDedupeService.Run(store, apply));
                store.Dispose();
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine(ex.StackTrace ?? "");
            }
        }
        WriteVerifyReport("dedupe_rows.txt", report);
    }

    /// <summary>
    /// 沙箱验收：造「同一份文件两行记录」（老行 missing=1 拿标签）→ 合并 → 断言
    /// （活行保住自己的标签并继承对方的、收藏/nsfw/手动分类跟着走、多余行删除、
    /// sidecar 与 sidecar_state 收尾、重复执行无组可处理）。
    /// </summary>
    private static void RunVerifyDedupeRows()
    {
        var report = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "ptv", "deduperows");
        var lib = Path.Combine(sandbox, "lib");
        var pass = 0;
        var fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++;
            else fail++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        try
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            AppPaths.ApplySandbox(sandbox);
            AppPaths.EnsureDirs();
            Directory.CreateDirectory(Path.Combine(lib, "cartoon", "case"));
            var pixel = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            File.WriteAllBytes(Path.Combine(lib, "cartoon", "case", "a.png"), pixel);

            var settings = new SettingsStore();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var (root, scan) = folders.AddRootAndScan(lib);
            report.AppendLine($"sandbox = {sandbox}");
            report.AppendLine($"图库    = [{root.Id}] {root.Path}  scan={scan.Summary()}");

            var live = store.QueryAll("SELECT * FROM images WHERE root_id=$r", ("$r", root.Id)).Single();
            report.AppendLine($"活行    = #{live.Id} {live.DirRel}/{live.Name}");
            store.SetCategorizedTags([live.Id], [("resolution", "1x1")]);

            // 老记录：目录大小写不同、missing=1，标签挂在它身上（真实数据就是这么来的）
            var phantomSidecar = Path.Combine(lib, "cartoon", "Case", StoreService.SidecarName);
            store.Exec("INSERT INTO images(root_id,rel_path,abs_path,dir_rel,name,ext,size,mtime,missing," +
                       "added_at,updated_at,managed) VALUES(" +
                       $"{root.Id},'cartoon/Case/{Sql(live.Name)}'," +
                       $"'{Sql(Path.Combine(lib, "cartoon", "Case", live.Name))}','cartoon/Case'," +
                       $"'{Sql(live.Name)}','.png',{live.Size}," +
                       live.Mtime.ToString("F9", System.Globalization.CultureInfo.InvariantCulture) +
                       ",1,1,1,1)");
            var phantomId = (int)RawLong(AppPaths.DbPath, "SELECT id FROM images WHERE dir_rel='cartoon/Case'");
            store.SetCategorizedTags([phantomId], [("work", "veri_work"), ("artist", "veri_artist")]);
            store.Exec($"INSERT OR IGNORE INTO favorites(image_id,added_at) VALUES({phantomId},1)");
            store.Exec("INSERT OR IGNORE INTO nsfw_scores(image_id,source,rating,value,nsfw_prob,confidence," +
                       $"margin,model_name,model_hash,detail,checked_at) VALUES({phantomId},'danbooru','s','',0,0,0,'','','',1)");
            store.Exec($"INSERT OR IGNORE INTO image_manual_categories(image_id,category) VALUES({phantomId},'resolution')");
            store.Exec($"INSERT OR REPLACE INTO sidecar_state(path,mtime,hash) VALUES('{Sql(phantomSidecar)}',1,NULL)");
            var imagesBefore = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM images");
            var linksBefore = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");
            report.AppendLine($"造出老行 #{phantomId}（work=veri_work / artist=veri_artist + 收藏 + nsfw 分）");
            report.AppendLine();

            var dry = RowDedupeService.Run(store, apply: false);
            Check("预演不写数据",
                RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM images") == imagesBefore &&
                RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags") == linksBefore);
            Check("预演认出了重复组", dry.Contains("重复组 1 组") && dry.Contains("可继承的标签关联 2 行"),
                dry.Contains("没有「同一路径多行」") ? "没认出重复组" : "");

            report.AppendLine("---- 执行 ----");
            report.AppendLine(RowDedupeService.Run(store, apply: true));
            var imagesAfter = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM images");
            var linksAfter = RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM image_tags");

            Check("只剩一行记录", imagesAfter == imagesBefore - 1, $"{imagesBefore} → {imagesAfter}");
            Check("missing 行没了", RawLong(AppPaths.DbPath, "SELECT COUNT(*) FROM images WHERE missing!=0") == 0);
            Check("活行继承了两个标签、保住自己的",
                RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM image_tags WHERE image_id={live.Id}") == 3 &&
                RawText(AppPaths.DbPath, "SELECT name FROM tags t JOIN image_tags it ON it.tag_id=t.id " +
                    $"WHERE it.image_id={live.Id} AND t.category='work'") == "veri_work" &&
                RawText(AppPaths.DbPath, "SELECT name FROM tags t JOIN image_tags it ON it.tag_id=t.id " +
                    $"WHERE it.image_id={live.Id} AND t.category='artist'") == "veri_artist" &&
                RawText(AppPaths.DbPath, "SELECT name FROM tags t JOIN image_tags it ON it.tag_id=t.id " +
                    $"WHERE it.image_id={live.Id} AND t.category='resolution'") == "1x1");
            Check("标签关联总数不变（只是换了归属）", linksAfter == linksBefore, $"{linksBefore} → {linksAfter}");
            Check("收藏跟着活行",
                RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM favorites WHERE image_id={live.Id}") == 1);
            Check("nsfw 分跟着活行",
                RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM nsfw_scores WHERE image_id={live.Id}") == 1);
            Check("手动分类跟着活行",
                RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM image_manual_categories WHERE image_id={live.Id}") == 1);
            Check("老目录的 sidecar 状态键被清掉",
                RawLong(AppPaths.DbPath, $"SELECT COUNT(*) FROM sidecar_state WHERE path='{Sql(phantomSidecar)}'") == 0);
            var sidecar = Path.Combine(lib, "cartoon", "case", StoreService.SidecarName);
            var sidecarWork = "";
            if (File.Exists(sidecar))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecar));
                if (doc.RootElement.GetProperty("files").TryGetProperty("a.png", out var entry) &&
                    entry.TryGetProperty("classified_tags", out var tags))
                    foreach (var tag in tags.EnumerateArray())
                        if (tag.GetProperty("category").GetString() == "work")
                            sidecarWork = tag.GetProperty("name").GetString() ?? "";
            }
            Check("sidecar 里写上了继承来的作品标签", sidecarWork == "veri_work", sidecarWork);
            Check("重复执行时没有组可处理",
                RowDedupeService.Run(store, apply: false).Contains("没有「同一路径多行」"));
            store.Dispose();
        }
        catch (Exception ex)
        {
            fail++;
            report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? "");
        }

        report.AppendLine($"结果: PASS {pass} / FAIL {fail}");
        WriteVerifyReport("verify_dedupe_rows.txt", report);
    }

    /// <summary>
    /// 整理目录（--organize [--apply]）。默认只预演；执行前自动备份库。
    /// 报告写到 %TEMP%\ptv\organize.txt。
    /// </summary>
    private static void RunOrganize(bool apply)
    {
        var report = new StringBuilder();
        var scope = AppPaths.Sandbox ?? AppPaths.LocalDataDir;
        if (!SingleInstance.TryAcquire(scope))
        {
            report.AppendLine("有另一个 Gallery 实例正在运行，请先退出再整理（重试一次即可）。");
        }
        else
        {
            try
            {
                var store = new StoreService(AppPaths.DbPath);
                var map = FolderMap.Load(AppPaths.FolderMapPath);
                var logPath = Path.Combine(Path.GetTempPath(), "ptv", "organize_moves.txt");
                report.AppendLine(OrganizeService.Run(store, map, apply, logPath));
                store.Dispose();
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine(ex.StackTrace ?? "");
            }
        }
        WriteVerifyReport("organize.txt", report);
    }

    /// <summary>v5 逐级目录模板：标签四级、来源、导入时间、兜底、多标签顺序、空模板。</summary>
    private static void RunVerifyFolderMap()
    {
        var report = new StringBuilder();
        void Check(string name, bool ok, string detail = "")
            => report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");

        try
        {
            var json = """
                {
                  "version": 5,
                  "root": "D:/Photos",
                  "tag_order": "input",
                  "levels": [
                    { "kind": "tag", "category": "kind", "use_tag_name": true, "fallback": "未分类", "date_part": "" },
                    { "kind": "tag", "category": "work", "use_tag_name": true, "fallback": "未分类", "date_part": "" },
                    { "kind": "tag", "category": "character", "use_tag_name": true, "fallback": "未分类", "date_part": "" },
                    { "kind": "tag", "category": "rating", "use_tag_name": true, "fallback": "未分级", "date_part": "" }
                  ]
                }
                """;
            var map = FolderMap.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement);
            Check("v5 读入", map.LoadError.Length == 0 && map.Levels.Count == 4 && map.Root == "D:/Photos", map.LoadError);
            var tags = new Dictionary<string, List<string>>
            {
                ["kind"] = ["动漫"], ["work"] = ["绝区零"], ["character"] = ["简"], ["rating"] = ["q"],
            };
            var got = map.Resolve(tags, new FolderMap.FolderContext("https://x.com/user/status/1", 0));
            Check("四级标签目录 + NSFW 分组", got == "动漫/绝区零/简/NSFW", got);

            var noTags = new Dictionary<string, List<string>>();
            var fallback = map.Resolve(noTags);
            Check("无标签走每级兜底", fallback == "未分类/未分类/未分类/未分级", fallback);

            var fixedMap = FolderMap.Parse(System.Text.Json.JsonDocument.Parse("""
                {"version":5,"root":"","tag_order":"input","levels":[
                  {"kind":"tag","category":"kind","use_tag_name":false,"fixed_name":"插画","fallback":"未分类","date_part":""}]}
                """).RootElement);
            var fixedResult = fixedMap.Resolve(new Dictionary<string, List<string>> { ["kind"] = ["动漫"] });
            Check("不勾选用固定名", fixedResult == "插画", fixedResult);
            Check("无该类型标签走兜底", fixedMap.Resolve(noTags) == "未分类", fixedMap.Resolve(noTags));

            var sourceDate = FolderMap.Parse(System.Text.Json.JsonDocument.Parse("""
                {"version":5,"root":"","tag_order":"input","levels":[
                  {"kind":"source","category":"","use_tag_name":true,"fixed_name":"","fallback":"未知来源","date_part":""},
                  {"kind":"date","category":"","use_tag_name":true,"fixed_name":"","fallback":"未知时间","date_part":"month"}]}
                """).RootElement);
            var ctx = new FolderMap.FolderContext("https://www.pixiv.net/artworks/123",
                new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds());
            var sd = sourceDate.Resolve(noTags, ctx);
            Check("来源站点 + 导入月份", sd == "pixiv/2026-11", sd);
            var unknown = sourceDate.Resolve(noTags, new FolderMap.FolderContext("https://example.com/a", 0));
            Check("未知站点用域名 + 时间兜底", unknown == "example.com/未知时间", unknown);

            var orderMap = FolderMap.Parse(System.Text.Json.JsonDocument.Parse(json.Replace("\"input\"", "\"text\"")).RootElement);
            var multi = orderMap.Resolve(new Dictionary<string, List<string>>
            {
                ["kind"] = ["乙", "甲"], ["work"] = ["作品"], ["character"] = ["角色"], ["rating"] = ["e"],
            });
            Check("文字顺序取第一个", multi == "乙/作品/角色/NSFW", multi);

            var empty = new FolderMap();
            Check("空模板进根目录", empty.Resolve(tags).Length == 0);
            var legacy = FolderMap.Parse(System.Text.Json.JsonDocument.Parse("""
                {"version":4,"managed_dirs":["cartoon"],"skip_dirs":[],"rules":[],
                 "routing_rules":[{"enabled":true,"category":"kind","name":"真人","folder":"真人",
                 "split_rating":true,"sfw_folder":"n","nsfw_folder":"h","append_artist":true}]}
                """).RootElement);
            Check("v4 备份可读但不参与计算", legacy.ManagedDirs.Count == 1 && legacy.Levels.Count == 0
                && legacy.Resolve(tags).Length == 0);
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }
        WriteVerifyReport("foldermap.txt", report);
    }

    /// <summary>
    /// 沙箱验收：造受管/跳过/不受管三类目录与各种标签 → 整理 → 断言
    /// （作品/画师分层、非法字符清洗、no_source 与 _无作者兜底、skip_dirs 与不受管目录不动、
    /// 预演不落盘、腾空旧目录连带 sidecar 一起收掉）。
    /// </summary>
    private static void RunVerifyOrganize()
    {
        var report = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "ptv", "organize");
        var lib = Path.Combine(sandbox, "lib");
        var pass = 0;
        var fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++;
            else fail++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        try
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            AppPaths.ApplySandbox(sandbox);
            AppPaths.EnsureDirs();
            var pixel = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            var files = new Dictionary<string, string[]>
            {
                ["cartoon/old_work"] = ["a.png", "b.png", "c.png", "d.png"],
                ["cartoon/skipdir"] = ["e.png"],
                ["cartoon/wizarding_world/_无作者"] = ["h.png"],   // 已经在目标位置，不该被搬
                ["真人"] = ["g.png"],
            };
            foreach (var (dir, names) in files)
            {
                Directory.CreateDirectory(Path.Combine(lib, dir.Replace('/', '\\')));
                foreach (var name in names)
                    File.WriteAllBytes(Path.Combine(lib, dir.Replace('/', '\\'), name), pixel);
            }
            File.WriteAllText(AppPaths.FolderMapPath, """
                {
                  "version": 3,
                  "managed_dirs": ["cartoon"],
                  "skip_dirs": ["cartoon/skipdir"],
                  "rules": [],
                  "_说明": "沙箱验收用"
                }
                """);

            var settings = new SettingsStore();
            settings.Load();
            var store = new StoreService(AppPaths.DbPath);
            var folders = new FolderService(store, settings);
            var (root, scan) = folders.AddRootAndScan(lib);
            var rows = store.QueryAll("SELECT * FROM images WHERE root_id=$r", ("$r", root.Id))
                .ToDictionary(r => r.DirRel + "/" + r.Name, r => r.Id);
            report.AppendLine($"sandbox = {sandbox}");
            report.AppendLine($"图库    = [{root.Id}] {root.Path}  scan={scan.Summary()}");
            report.AppendLine();

            int Id(string rel) => rows[rel];
            store.SetCategorizedTags([Id("cartoon/old_work/a.png")],
                [("work", "honkai:_star_rail"), ("artist", "mr_goblin")]);
            store.SetCategorizedTags([Id("cartoon/old_work/b.png")], [("kind", "original")]);
            store.SetCategorizedTags([Id("cartoon/old_work/c.png")], [("work", "wizarding_world")]);
            store.SetCategorizedTags([Id("cartoon/old_work/d.png")],
                [("work", "goddess_of_victory:_nikke"), ("artist", "@cheese_egg0v0")]);
            store.SetCategorizedTags([Id("cartoon/skipdir/e.png")], [("work", "elden_ring")]);
            store.SetCategorizedTags([Id("真人/g.png")], [("work", "elden_ring"), ("artist", "someone")]);
            store.SetCategorizedTags([Id("cartoon/wizarding_world/_无作者/h.png")], [("work", "wizarding_world")]);

            var map = FolderMap.Load(AppPaths.FolderMapPath);
            Check("规则读进来了", map.ManagedDirs.Count == 1 && map.SkipDirs.Count == 1,
                $"受管 {string.Join("、", map.ManagedDirs)} / 跳过 {string.Join("、", map.SkipDirs)}");
            Check("目录名清洗：冒号去掉、@ 去掉、统一小写",
                FolderMap.Sanitize("goddess_of_victory:_nikke") == "goddess_of_victory_nikke" &&
                FolderMap.Sanitize("@cheese_egg0v0") == "cheese_egg0v0" &&
                FolderMap.Sanitize("LoveLive!") == "lovelive!");
            Check("兜底：没作品→no_source、没画师→_无作者",
                map.TargetDirRel([], [], "cartoon") == "cartoon/no_source/_无作者" &&
                map.TargetDirRel(["fate"], [], "cartoon") == "cartoon/fate/_无作者");
            Check("不受管的目录不参与",
                map.TargetDirRel("真人", ["elden_ring"], ["someone"]) is null);

            var dry = OrganizeService.Run(store, map, apply: false);
            Check("预演认出要搬 4 张、跳过 1 张",
                dry.Contains("受管 5 张（不动 1、要搬 4）") && dry.Contains("不受管 2 张"),
                dry.Contains("== 盘点 ==") ? dry.Split("== 盘点 ==")[1].Split('\n')[1].Trim() : "");
            Check("预演没落盘",
                File.Exists(Path.Combine(lib, "cartoon", "old_work", "a.png")) &&
                !Directory.Exists(Path.Combine(lib, "cartoon", "honkai_star_rail")));

            report.AppendLine("---- 执行 ----");
            var logPath = Path.Combine(sandbox, "moves.txt");
            report.AppendLine(OrganizeService.Run(store, map, apply: true, logPath));

            foreach (var (from, to) in new[]
                     {
                         ("cartoon/old_work/a.png", "cartoon/honkai_star_rail/mr_goblin/a.png"),
                         ("cartoon/old_work/b.png", "cartoon/no_source/_无作者/b.png"),
                         ("cartoon/old_work/c.png", "cartoon/wizarding_world/_无作者/c.png"),
                         ("cartoon/old_work/d.png", "cartoon/goddess_of_victory_nikke/cheese_egg0v0/d.png"),
                     })
            {
                var id = Id(from);
                var dirRel = RawText(AppPaths.DbPath, $"SELECT dir_rel FROM images WHERE id={id}");
                var target = to[..to.LastIndexOf('/')];
                Check($"#{id} {from} → {to}",
                    dirRel == target && File.Exists(Path.Combine(lib, to.Replace('/', '\\'))),
                    $"库={dirRel} 盘={(File.Exists(Path.Combine(lib, to.Replace('/', '\\'))) ? "在" : "不在")}");
            }
            Check("跳过目录里的图没动",
                File.Exists(Path.Combine(lib, "cartoon", "skipdir", "e.png")) &&
                RawText(AppPaths.DbPath, $"SELECT dir_rel FROM images WHERE id={Id("cartoon/skipdir/e.png")}") == "cartoon/skipdir");
            Check("已经在目标位置的图不动",
                RawText(AppPaths.DbPath, $"SELECT dir_rel FROM images WHERE id={Id("cartoon/wizarding_world/_无作者/h.png")}") == "cartoon/wizarding_world/_无作者");
            Check("不受管目录里的图没动",
                File.Exists(Path.Combine(lib, "真人", "g.png")) &&
                RawText(AppPaths.DbPath, $"SELECT dir_rel FROM images WHERE id={Id("真人/g.png")}") == "真人");
            Check("腾空的旧目录被清掉（目录与 sidecar 都不在）",
                !Directory.Exists(Path.Combine(lib, "cartoon", "old_work")) &&
                !File.Exists(Path.Combine(lib, "cartoon", "old_work", StoreService.SidecarName)));
            Check("新目录的 sidecar 已生成",
                File.Exists(Path.Combine(lib, "cartoon", "honkai_star_rail", "mr_goblin", StoreService.SidecarName)));
            Check("保留目录的 sidecar 还在",
                File.Exists(Path.Combine(lib, "cartoon", "skipdir", StoreService.SidecarName)));
            Check("搬迁清单落盘（4 张，含新←旧路径）",
                File.Exists(logPath) &&
                File.ReadAllLines(logPath).Count(line => !line.StartsWith("#")) == 4 &&
                File.ReadAllLines(logPath).Any(line => line.Contains("honkai_star_rail") && line.Contains("old_work")));
            store.Dispose();
        }
        catch (Exception ex)
        {
            fail++;
            report.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? "");
        }

        report.AppendLine($"结果: PASS {pass} / FAIL {fail}");
        WriteVerifyReport("verify_organize.txt", report);
    }
}
