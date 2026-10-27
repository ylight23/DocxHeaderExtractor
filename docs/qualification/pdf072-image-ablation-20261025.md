# PDF072 text-only versus page-image ablation

Run date: 2026-10-25. Two fresh gpt-6-luna classifications used the exact frozen production request packets for `SEG-012` and `SEG-013` (59 targets across pages 13–14). Both arms saw the same embedded production instruction, target text, context, facts, styles, tools, and response schema. The image arm additionally viewed the complete rendered page image for each page. Neither arm saw Gold or prior model outputs. Both responses were frozen before Gold scoring.

## Results

| Arm | Selected | TP | FP | FN | Precision | Recall |
|---|---:|---:|---:|---:|---:|---:|
| Packet text only | 33 | 12 | 21 | 0 | 36.4% | 100.0% |
| Same packet plus page image | 12 | 10 | 2 | 2 | 83.3% | 83.3% |

Gold has 12 heading aliases within these 59 targets. The image arm removed all seven false positives from the earlier native Luna run, along with 12 other false positives in the text-only arm. It also dropped two Gold heading aliases: `L0513:S0` and `L0514:S0`, the document title pair at the top of page 13. It retained two false positives: `L0546:S1` (“Fine Tuning Global Linking Procedures”) and `L0550:S0` (“Improving Reliability of Price Comparisons”). The image-arm explanations treated these as headings because each introduces indented topics underneath it; Gold classifies both as agenda entries, not document headings.

## What this tests

The page image materially changed Luna's decisions. In the image arm, it described the timed presentations as schedule entries, while retaining the day and shaded session labels as agenda structure. That is direct evidence that the presentation of the page helps this model distinguish the regular rows. It does not show that images solve the problem: the image arm became much more conservative, missed the two-part document title, and still promoted two timed agenda topics that have indented subitems.

This was a classification ablation using frozen production request contents, not a run through `PdfHandler` and not a provider API call. One fresh Luna response per arm is not enough to estimate run-to-run variance or production accuracy. The image was provided through the Luna session's visual input, so this tests access to the page presentation; it does not measure OpenRouter image-token cost, latency, or wire-format integration.

## Reproducibility

- Original request hashes, response hashes, image hashes, and metrics: [comparison.json](../artifacts/qualification/pdf072-image-ablation-20261025/comparison.json).
- Blind text-only decisions: [text-arm](../artifacts/qualification/pdf072-image-ablation-20261025/text-arm/).
- Blind image decisions: [image-arm](../artifacts/qualification/pdf072-image-ablation-20261025/image-arm/).
- Rendered inputs: `artifacts/qualification/pdf072-image-ablation-20261025/page-13.png` and `page-14.png`.
- Gold authority: `DOC-0252`, SHA-256 `e966d4e57ba6b312a2cebe93446c4350d4ef7173458f7c528306775f9c743b03`.
