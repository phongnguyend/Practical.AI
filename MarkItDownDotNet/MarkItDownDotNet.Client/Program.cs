using System.Net.Http.Headers;

HttpClient client = new HttpClient();

var filePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\phongnguyend.pdf";
var resultFilePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\phongnguyend.md";

using var form = new MultipartFormDataContent();
using var fileContent = new ByteArrayContent(File.ReadAllBytes(filePath));
fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
form.Add(fileContent, "formFile", Path.GetFileName(filePath));
form.Add(new StringContent("Test Name"), "name");

var response = await client.PostAsync($"https://localhost:7110/", form);
response.EnsureSuccessStatusCode();

var markdown = await response.Content.ReadAsStringAsync();

File.WriteAllText(resultFilePath, markdown);

Console.WriteLine(markdown);

Console.ReadLine();