using System.Net.Http.Headers;

HttpClient client = new HttpClient();

var filePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\phongnguyend.pdf";

using var form = new MultipartFormDataContent();
using var fileContent = new ByteArrayContent(File.ReadAllBytes(filePath));
fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
form.Add(fileContent, "formFile", Path.GetFileName(filePath));
form.Add(new StringContent("Test Name"), "name");

var response = await client.PostAsync($"https://localhost:7110/", form);
response.EnsureSuccessStatusCode();

var createdFile = await response.Content.ReadAsStringAsync();
Console.WriteLine(createdFile);

Console.ReadLine();