namespace DocxHeaderExtractor.DocumentProcessing.Policy;

public interface IPostClassificationPolicy
{
    PostClassificationDecision Decide(PostClassificationInput input);
}
