namespace Control.Model.Response;

public sealed record ValidationErrorResponse(IReadOnlyDictionary<string, string[]> Errors);
