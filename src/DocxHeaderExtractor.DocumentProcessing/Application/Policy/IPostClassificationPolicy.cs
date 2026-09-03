namespace DocxHeaderExtractor.Core.Application.Policy;

public interface IPostClassificationPolicy
{
    PostClassificationDecision Decide(PostClassificationInput input);
}
