# Callout — AutoCAD Detail Extraction

Plugin AutoCAD viết bằng C# để trích chi tiết từ block, phóng tỷ lệ, tạo XClip, đường viền, đường nối và kích thước tự động.

Kết quả dùng `BlockReference` trực tiếp với XClip và một polyline viền riêng. Plugin không tạo block trung gian có tên `CT_DETAIL_*`.

## Yêu cầu

| Thành phần | Cấu hình hiện tại |
| --- | --- |
| Hệ điều hành | Windows x64 |
| Target framework | .NET Framework 4.8 (`net48`) |
| Ngôn ngữ / giao diện | C# 7.3 / WPF |
| CAD series trong manifest | `R24.0` đến `R24.3` |
| Host đã chạy kiểm thử | AutoCAD Core Console 2021 |
| Reference API | Các package `AutoCAD.NET`, `AutoCAD.NET.Core`, `AutoCAD.NET.Model` phiên bản `23.1.0` |

Để build, cần .NET SDK/MSBuild hỗ trợ WPF và reference assemblies của .NET Framework 4.8. Các package Autodesk được restore qua NuGet; runtime DLL của AutoCAD không được đóng gói vào bundle.

## Các lệnh

| Lệnh | Chức năng |
| --- | --- |
| `CT` | Trích chi tiết, tạo polyline nối từ khung gốc đến khung chi tiết và hai đầu tròn liên kết. |
| `CT1` | Trích chi tiết có bubble tại nguồn và tiêu đề chi tiết. |
| `CT2` | Chọn block khung bản vẽ và các attribute dùng cho số tờ, tỷ lệ. |
| `CTS` | Xem danh sách liên kết giữa block nguồn và chi tiết `CT1`, chọn mục để zoom đến đối tượng. |
| `CTFIXCROP` | Bổ sung tham chiếu cho spatial filter hiện có để hỗ trợ copy crop sang DWG khác. |

Ribbon **TH Tools → Detail Callout** cung cấp các nút CT, CT1, CTS và CT2.

## Sử dụng CT và CT1

1. Chạy `CT2` nếu cần tự lấy số tờ và dimscale từ block khung. Chọn block khung có attribute, chọn tag số tờ và tag tỷ lệ, rồi lưu thiết lập.
2. Chạy `CT` hoặc `CT1` và chọn một `BlockReference` nguồn.
3. Chọn đường bao khép kín: polyline, circle hoặc curve khép kín phù hợp.
4. Di chuyển chuột để đặt chi tiết. Trong lúc preview:
   - `↑`, `W` hoặc `+`: tăng tỷ lệ thêm 1.
   - `↓`, `S` hoặc `-`: giảm tỷ lệ bớt 1, tối thiểu 1.
   - `Esc`: hủy thao tác.
5. Chọn vị trí đặt. Nếu không tìm được tỷ lệ từ block khung, nhập dimscale khi được hỏi.

Plugin tạo bản sao instance của block bằng `DeepCloneObjects`, gắn XClip, thêm polyline viền và dimension từ hình học trong vùng trích. `CT1` thêm bubble số chi tiết và số tờ; liên kết được lưu cùng DWG bằng Xrecord.

Chi tiết vẫn dùng chung định nghĩa block với nguồn. Chỉnh định nghĩa bằng `BEDIT` có thể ảnh hưởng cả nguồn và các chi tiết dùng định nghĩa đó.

Thiết lập `CT2` hiện được lưu trong bộ nhớ của phiên AutoCAD; khi khởi động phiên mới, cần chọn lại thiết lập. Liên kết `CT1` đã ghi vào DWG vẫn được giữ khi lưu/mở file.

## Chỉnh polyline nối bằng tay

Đường nối của `CT` gồm một polyline và hai polyline tròn được liên kết với vertex đầu/cuối:

- Chọn đường nối, kéo grip hoặc sửa vertex bằng các công cụ AutoCAD thông thường.
- Khi hoàn tất thao tác, hai đầu tròn tự về đúng vertex đầu và cuối.
- Có thể thêm hoặc chỉnh vertex giữa; plugin giữ nguyên hình dạng đường bạn đã chỉnh.
- Copy đường nối tạo liên kết riêng cho bản copy, tránh di chuyển nhầm đầu tròn của bản gốc.
- Liên kết được lưu cùng DWG. Tính năng tự cập nhật đầu tròn cần plugin được nạp trong phiên AutoCAD đang chỉnh sửa.

Khi tạo mới, đường nối bám vào đường bao thật, ưu tiên ít góc gấp và tránh đi xuyên khung. Khi khung nguồn và khung chi tiết chồng lên hoặc chạm nhau, plugin không vẽ đường nối.

Các đường nối cũ chưa có metadata liên kết cần được tạo lại bằng bản plugin mới để hai đầu tròn tự bám vertex.

## Copy chi tiết sang DWG khác

Chi tiết mới có tham chiếu bền vững tới `ACAD_FILTER/SPATIAL` để giữ XClip qua các bước clone và lưu/mở DWG. Chọn thêm polyline viền, dimension và annotation nếu muốn mang theo toàn bộ phần trình bày.

Với chi tiết tạo bằng bản plugin cũ:

1. Mở file nguồn còn XClip.
2. Chạy `CTFIXCROP`.
3. Lưu DWG rồi copy sang file đích.

`CTFIXCROP` xử lý filter còn tồn tại; lệnh không khôi phục đường bao của XClip đã bị mất hoàn toàn. Trong trường hợp đó, cần copy lại từ file nguồn còn crop.

## Build và cài đặt

Chạy PowerShell tại thư mục gốc repository:

```powershell
dotnet restore .\Callout\Callout.csproj
dotnet build .\Callout\Callout.csproj -c Release -p:Platform=x64 -f net48 --no-restore "-p:AppData=$env:APPDATA"
```

**Build có auto-deploy:** target `DeployBundle` chạy sau mỗi lần build và chép bundle vào `%APPDATA%\Autodesk\ApplicationPlugins\Callout.bundle`. Hãy lưu bản vẽ và đóng AutoCAD trước khi build bản cập nhật để giải phóng khóa DLL.

DLL build nằm tại `Callout\bin\x64\Release\Callout.dll`. Bundle có cấu trúc:

```text
Callout.bundle/
├── PackageContents.xml
└── Contents/
    ├── Callout.dll
    └── Resource/
```

Mở lại AutoCAD để nạp bundle. Có thể dùng `NETLOAD` với `Contents\Callout.dll` để nạp thủ công; giữ thư mục `Resource` cạnh DLL để các icon hoạt động.

### Build kiểm tra vào thư mục tạm

Đổi giá trị `AppData` để target auto-deploy chép bundle vào thư mục staging thay vì cập nhật bản đang cài:

```powershell
$staging = Join-Path $env:TEMP 'Callout-Build'
dotnet build .\Callout\Callout.csproj -c Release -p:Platform=x64 -f net48 --no-restore "-p:AppData=$staging"
```

Bundle kiểm tra nằm trong `$staging\Autodesk\ApplicationPlugins\Callout.bundle`.

## Kiểm thử

### Hình học thuần

Cần PowerShell 7 (`pwsh`). Bộ kiểm thử biên dịch trực tiếp các lớp hình học, không cần nạp AutoCAD:

```powershell
pwsh -NoProfile -File .\tests\Test-CalloutClipPolygon.ps1
```

Bao gồm 26 kiểm tra polygon và 16 tình huống đường nối: khung lõm, tự cắt, giao điểm với đường bao, đầu nối trên biên, không xuyên khung và các vị trí bố trí khác nhau.

### AutoCAD Core Console

Build plugin trước, sau đó build assembly kiểm thử riêng:

```powershell
dotnet restore .\tests\AutoCAD\Callout.NativeTests.csproj
dotnet build .\tests\AutoCAD\Callout.NativeTests.csproj -c Release -p:Platform=x64 --no-restore
```

Chỉ định đường dẫn Core Console và template có trên máy. Ví dụ với thư mục cài AutoCAD 2021 mặc định:

```powershell
$cadRoot = Join-Path $env:ProgramFiles 'Autodesk\AutoCAD 2021'
$console = Join-Path $cadRoot 'accoreconsole.exe'
$template = Join-Path $cadRoot 'Sample\Sheet Sets\Architectural\IRD.dwt'
pwsh -NoProfile -File .\tests\Test-CropRoundTrip.ps1 -CoreConsolePath $console -TemplatePath $template
```

Script chạy Core Console ẩn với profile riêng, mở template ở chế độ chỉ đọc và tạo DWG kiểm thử trong `%TEMP%`. Đường dẫn log được in khi chạy. Các lệnh kiểm thử chỉ thuộc assembly `Callout.NativeTests`, không được đóng gói vào plugin production.

Các ca kiểm thử native bao gồm:

- Giữ spatial filter sau khi lưu/mở DWG và clone qua hai database.
- Block dịch chuyển, quay/phóng tỷ lệ, mirror và crop cũ được sửa.
- Sửa vertex đầu/cuối, thêm vertex giữa và cập nhật hai đầu tròn.
- Copy riêng đường nối, copy cả bộ đối tượng và WBLOCK sang database khác.
- Lưu/mở liên kết đầu tròn, callback cập nhật sau lệnh và Undo/Redo.

Build và các ca Core Console đã được xác minh trên AutoCAD 2021. Preview khi kéo chuột, thao tác grip trực tiếp, các phiên bản host khác và block dynamic/annotative thực tế cần được kiểm tra trong giao diện AutoCAD với dữ liệu sử dụng.

## Cấu trúc mã nguồn

| File | Vai trò |
| --- | --- |
| `CalloutCommand.cs` | Đăng ký các lệnh AutoCAD. |
| `CalloutCoreLogic.cs` | Điều phối trích chi tiết, transaction, XClip và dimension. |
| `CalloutJig.cs` | Preview bằng `DrawJig`/`PushClipBoundary` và xử lý phím tỷ lệ. |
| `CalloutClipPolygon.cs` | Validation polygon, kiểm tra điểm và giao cắt hình học thuần. |
| `CalloutLeaderRouter.cs` | Chọn đường nối vuông góc giữa hai đường bao. |
| `CalloutGeometryService.cs` | Tạo polyline nối và hai đầu tròn trong bộ nhớ. |
| `CalloutLeaderLinks.cs` | Lưu liên kết và đồng bộ đầu tròn theo vertex. |
| `CalloutCropStorage.cs` | Lưu tham chiếu bền vững tới spatial filter. |
| `CalloutMappingRepository.cs` | Lưu/đọc liên kết bubble `CT1` trong DWG. |
| `CalloutServices.cs` | Cấu hình phiên làm việc và watcher cập nhật đối tượng. |
| `CalloutHelpers.cs` | Helper block, attribute, layer, style và title block. |
| `RibbonSetup.cs` | Lifecycle plugin và Ribbon. |
| `tests/` | Kiểm thử hình học và kiểm thử native trong Core Console. |

## Giới hạn hiện tại

- Khung trích phải khép kín và nằm trên mặt phẳng song song XY trong WCS và hệ tọa độ block.
- Circle và các đoạn cong được xấp xỉ bằng polygon khi tạo clip; đỉnh góc của polyline được giữ lại.
- Circle/arc sau scale không đều được xử lý cho dim tuyến tính; plugin bỏ dim bán kính/đường kính khi hình đã trở thành dạng ellipse.
- Liên kết `CT1` và đầu tròn chỉ được tự nhận diện khi đã có metadata của bản plugin mới; dữ liệu cũ không được ghép cặp bằng suy đoán vị trí.

Repository: [hvtam9archi-cpu/Callout](https://github.com/hvtam9archi-cpu/Callout).
