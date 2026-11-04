"""Plain asserts for the TextTiling scores: python -I test_texttiling.py (run from this folder)."""
from texttiling import cosine, depth_scores, tokens


def test_stop_words_and_digits_do_not_count():
    assert tokens("Và của 2026 báo cáo") == tokens("báo cáo")


def test_cosine_identical_and_disjoint():
    assert abs(cosine(tokens("hỗ trợ người bệnh"), tokens("hỗ trợ người bệnh")) - 1) < 1e-9
    assert cosine(tokens("hỗ trợ"), tokens("quỹ thu")) == 0.0


def test_the_deepest_gap_is_the_topic_change():
    topic_a = ["hỗ trợ người bệnh khó khăn", "người bệnh được hỗ trợ viện phí", "hỗ trợ người bệnh lao"]
    topic_b = ["quỹ thu chi tài chính", "thu chi quỹ hằng tháng", "tài chính quỹ kế toán"]
    depths = depth_scores(topic_a + topic_b, k=3)
    assert depths.index(max(depths)) == 2   # gap before paragraph 3, the change of topic


def test_repeated_paragraphs_give_no_depth():
    assert all(d == 0 for d in depth_scores(["truyền thông"] * 6, k=2))


if __name__ == "__main__":
    for name, fn in list(globals().items()):
        if name.startswith("test_"):
            fn()
            print("ok", name)
