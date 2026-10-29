# yzip 1.0.22

## 네이티브 가속 실험 경로

- Windows x64 패키지에 공식 7-Zip 26.04 `7z.dll`을 호출하는 in-process 브리지 `yzip_accel.dll`을 포함합니다.
- plain ZIP 해제, plain LZMA/LZMA2 7z 해제, 새 plain ZIP 생성 경로를 구현했지만 각각의 성능 enablement gate를 통과하지 못해 모두 기본 비활성입니다. 기본 동작은 기존 엔진을 그대로 사용합니다.
- 진단용 opt-in은 `YZIP_NATIVE_ZIP_EXTRACT=1`, `YZIP_NATIVE_7Z_EXTRACT=1`, `YZIP_NATIVE_ZIP_CREATE=1`입니다. `YZIP_DISABLE_ACCEL=1`은 세 opt-in을 모두 무시하고 네이티브 가속을 전역 비활성화합니다.
- 암호화, 분할 압축, 지원하지 않는 필터나 방식, DLL 누락·ABI 불일치 등 적용할 수 없는 입력은 작업 전체를 기존 엔진으로 처리합니다.

## 패키징과 검증

- 7-Zip 26.04 바이너리·소스 정보와 SHA-256 pin을 `7zip-pins.json`으로 고정하고, 7-Zip 라이선스 문서를 패키지에 포함합니다.
- 릴리스 워크플로에서 `7z.dll`, `yzip_accel.dll`, pin manifest, 라이선스의 포함 여부와 capability를 검사합니다.
- packaged smoke는 DLL ABI·세 capability·전역 비활성화 동작을 확인합니다.

## 성능 측정 결과

- 최종 매트릭스는 5개 데이터셋 × 2개 프로그램 × ZIP/7z × 압축/해제 × 요청 스레드 1·4·8·16의 160개 조건입니다. 각 조건은 준비 실행 1회와 새 프로세스 측정 7회를 사용하고, 생성 결과를 반대 프로그램으로 해제해 SHA-256 파일 트리를 검증했습니다.
- 요청 8스레드의 end-to-end wall-time 기하평균에서 yzip/Bandizip 비율은 ZIP 압축 3.1381배, ZIP 해제 3.5101배, 7z 압축 2.8912배, 7z 해제 6.7017배였습니다. 프로세스 시작 시간이 포함되며 내부 실제 worker 수를 측정한 값은 아닙니다.
- 과거 절대시간 7z 회귀 gate는 외부 영상 처리 부하가 있던 측정에서 5개 중 2개 데이터셋이 실패했습니다. 같은 실행의 Bandizip 비율로 정규화한 release gate는 5개 모두 +5% 이내로 통과했습니다. 이 release gate 통과는 개별 네이티브 경로를 기본 활성화한다는 의미가 아닙니다.
- plain 7z 해제와 plain ZIP 생성의 별도 성능 gate에서는 네이티브 경로가 기존 경로보다 빨라지지 않아 opt-in 상태를 유지합니다. 상세 조건과 원본 artifact 식별자는 `docs/performance/native-results.md`에 기록했습니다.

## 탐색기 메뉴와 배포

- GitHub hosted runner에서 Explorer의 in-process COM 메뉴 열거와 제품 DLL 검증을 분리했습니다. 등록값은 별도로 검사하고, 동일 DLL의 `IContextMenu` 로직은 직접 probe합니다.
- probe의 `CoInitializeEx` 결과 처리와 모든 실패 경로의 COM 객체·PIDL·메뉴·DLL 정리를 보완했습니다.
- 이번 Windows 앱과 설치 파일은 코드 서명 없이 빌드합니다. 코드 서명 서비스나 인증서는 추가하지 않았습니다.
