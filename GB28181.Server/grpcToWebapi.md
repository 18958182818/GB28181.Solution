# grpc to webapi ���췽��

1.  �������
1.  nuget �������������ӳ����Դ �������������������
  - ���ƣ�DotNet5 dev repository
  Դ��https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet5/nuget/v3/index.json
  - ��������� Microsoft.AspNetCore.Grpc.HttpApi  �ҵİ汾�ǣ�0.1.0-alpha.20254.1
1. ���2��google  api�����á����Դ���Ŀ��copy
annotations.proto��http.proto
**ע�⣺** ��������ļ���·���޸�annotations.proto�е�import����http.proto��·����descriptor.proto����·������
1. ����grpc�����proto
����ѡ��PtzControl.proto������
 + ���� import "Protos/google/api/annotations.proto";
 + ����rpc ��Ӵ���
 ԭʼ��
 `rpc PtzDirect (PtzDirectRequest) returns (PtzDirectReply) {}`
 �����
 
    rpc PtzDirect (PtzDirectRequest) returns (PtzDirectReply) {
            option (google.api.http) = {
              post: "/v1/PtzControl/PtzDirect"
              body: "*"
            };+
          }`
**˵����** ν�ʣ�post��url�����Զ��壬��Ϊ��������Ǹ������ͣ�ѡ��post������bodyѡ��ͨ���

1. �������ʱ��������
��startup.cs�ļ�ConfigureServices������������ã�
`services.AddGrpcHttpApi();`
1. ���༭launchSettings
·����Properties/launchSettings.json
Ŀ��������iis��������
Դ�ļ����£�


    {
      "iisSettings": {
        "windowsAuthentication": false,
        "anonymousAuthentication": true,
        "iisExpress": {
          "applicationUrl": "http://localhost:60500/",
          "sslPort": 44316
        }
      },
      "profiles": {
        "IIS Express": {
          "commandName": "IISExpress",
          "launchBrowser": true,
          "environmentVariables": {
            "ASPNETCORE_ENVIRONMENT": "Development"
          }
        },
        "GB28181.Service": {
          "commandName": "Project",
          "launchBrowser": true,
          "applicationUrl": "https://localhost:5001;http://localhost:5000",
          "environmentVariables": {
            "ASPNETCORE_ENVIRONMENT": "Development"
          }
        }
      }
    }
