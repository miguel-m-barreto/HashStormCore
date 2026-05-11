// src/HashStormCore/Api/Responses/ResultResponse.cs
namespace HashStormCore.Api.Responses;

public class ResultResponse<T>
{
    public ResultResponse(T result)
    {
        Result = result;
        Success = result is not null;
    }

    public ResultResponse()
    {
        Success = true;
    }

    public T Result { get; set; }
    public bool Success { get; set; }
}
