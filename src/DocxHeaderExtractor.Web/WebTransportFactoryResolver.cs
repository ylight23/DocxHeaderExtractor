using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Web;

/// <summary>Host composition seam; the default resolver retains the existing provider policy.</summary>
public delegate IInferenceTransportFactory WebTransportFactoryResolver(InferenceProviderSelection selection);
