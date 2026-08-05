using DocxHeaderExtractor.Web;

namespace DocxHeaderExtractor.Tests;

public sealed class WritebackStoreTests
{
    [Fact]
    public void An_entry_can_only_be_taken_once()
    {
        var store = new WritebackStore();
        var id = Guid.NewGuid();
        store.Put(id, [1, 2, 3], "ra.docx");

        Assert.Equal("ra.docx", store.Take(id)!.FileName);
        Assert.Null(store.Take(id));
    }

    [Fact]
    public void Unknown_run_ids_return_nothing_rather_than_throwing()
    {
        Assert.Null(new WritebackStore().Take(Guid.NewGuid()));
    }

    [Fact]
    public void Oldest_entries_are_evicted_once_the_cap_is_reached()
    {
        var store = new WritebackStore();
        var ids = new List<Guid>();

        // Trần là 8 mục; mục thứ 9 phải đẩy mục đầu tiên ra khỏi bộ nhớ.
        for (var i = 0; i < 9; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            store.Put(id, [(byte)i], $"{i}.docx");
            Thread.Sleep(2);   // Created dùng làm khoá sắp xếp; cần mốc thời gian phân biệt được
        }

        Assert.Null(store.Take(ids[0]));
        Assert.NotNull(store.Take(ids[8]));
    }

    [Fact]
    public void A_single_oversized_document_does_not_stay_resident()
    {
        var store = new WritebackStore();
        var big = Guid.NewGuid();
        store.Put(big, new byte[65 * 1024 * 1024], "qua-lon.docx");

        // Vượt trần tổng dung lượng thì bị loại ngay, thay vì giữ mãi một khối 65 MB.
        Assert.Null(store.Take(big));
    }
}
