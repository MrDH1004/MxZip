# MxZip 1.0.1

프로그램 안팎에 남아 있던 이전 이름 yzip을 모두 MxZip으로 바꿨습니다.

## 바뀐 점

- 설치 경로: `%LOCALAPPDATA%\Programs\MxZip\MxZip.exe` (이전 `Programs\yzip\yzip.exe`)
- 설정 폴더: `%LOCALAPPDATA%\MxZip`, 레지스트리: `HKCU\Software\MxZip`
- 탐색기 메뉴·파일 연결·기본 앱 목록에 등록되는 이름도 MxZip으로 바뀝니다.

## 이전 버전에서 넘어올 때

- 설치하면 이전 yzip(또는 1.0.0) 설치를 먼저 조용히 제거하고 새 경로에 설치합니다. 이전 탐색기 메뉴와 파일 연결도 정리됩니다.
- 설정(`%LOCALAPPDATA%\yzip\settings.json`)은 MxZip을 처음 실행할 때 그대로 가져옵니다.
- 1.0.0을 쓰고 있으면 자동 업데이트로 받을 수 있습니다. yzip 1.0.23 이하에서는 이 설치 파일을 직접 실행해 주세요.
- 파일 연결을 쓰던 경우 설치 화면의 "압축 파일을 MxZip으로 열기"를 켜 두면 새 이름으로 다시 연결합니다.

## 그 밖에

- 앱과 설치 파일은 코드 서명 없이 배포합니다.
