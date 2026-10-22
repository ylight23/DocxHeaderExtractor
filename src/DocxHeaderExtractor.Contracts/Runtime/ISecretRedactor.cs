namespace DocxHeaderExtractor.Contracts.Runtime;

public interface ISecretRedactor
{
    string Redact(string value);
}
