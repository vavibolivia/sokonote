# 소코 업무정리 (SokoNote)

Windows용 업무 일지 앱입니다. 주제(프로젝트)별로 업무를 날짜별 카드나 **주제당 한 장**으로 기록하고, Claude에게 내 업무 내용을 바탕으로 질문할 수 있습니다.

## 다운로드

**[최신 버전 받기 (SokoNote-win-x64.zip)](https://github.com/vavibolivia/sokonote/releases/latest/download/SokoNote-win-x64.zip)**

1. zip을 받아 원하는 폴더에 풉니다.
2. `SokoNote.exe`를 실행합니다. (.NET 설치 불필요 · Windows 10/11 64비트)

`main`에 변경이 올라갈 때마다 자동으로 테스트·빌드되어 [Releases](https://github.com/vavibolivia/sokonote/releases)의 `latest`가 갱신됩니다.

## 주요 기능

- **주제 목록** (왼쪽): 색상·순서 변경, 일지 개수 표시
- **보기 3가지**: **한 장**(기본) · 날짜별 · 전체 — 마지막에 쓴 보기와 주제를 기억합니다
  - **한 장**: 주제마다 하나의 문서에 이어서 기록합니다. 날짜가 필요 없는 업무도 그대로 적을 수 있도록 **날짜는 자동으로 넣지 않습니다.**
  - 날짜가 필요하면 **[오늘 날짜 넣기]** (Ctrl+Shift+D, 한 장에서 Ctrl+N) → `10월 22일 (목)` 구분선(해가 바뀌면 `2026년`)이 생기고 그 아래로 커서가 이동합니다. 구분선은 지우거나 고칠 수 없습니다.
  - 날짜가 있는 페이지는 ◀ ▶·달력(점 표시)·날짜 목록으로 그 날짜 위치로 이동합니다.
  - 검색창에 `10월 22일`, `10/22`, `2026-10-22`를 입력하고 Enter → 그 날짜로 이동, 일반 단어는 Enter마다 다음 위치를 선택(서식이 섞인 글자도 찾음)
- **검색**: 일지 카드와 한 장 페이지를 함께 찾고, 결과를 열면 찾은 글자에 커서가 놓입니다
- **Word 수준 서식**: 굵게·기울임·밑줄·취소선, 글꼴·크기, 글자색·형광펜, 정렬·줄 간격, 종이 색·줄/모눈/점 무늬
- **링크**: URL 자동 링크, Ctrl+K 삽입, Ctrl+클릭으로 열기 (http/https/mailto와 로컬 문서만 열고, 실행 파일은 탐색기에서 위치만 표시)
- **Claude에게 묻기**
  - **웹에서 묻기 (API 불필요)**: 앱 안의 브라우저로 claude.ai에 로그인해 내 Claude 계정으로 질문. 선택한 범위의 업무 내용을 입력창에 넣어 주며, 보내기는 직접 확인 후 Enter.
  - **Claude Code 연동**: 설치된 Claude Code CLI(구독 계정 또는 API 키)로 앱 안에서 바로 답변. 도구 사용은 모두 꺼져 있습니다.
- **데이터**: `%APPDATA%\SokoNote` 의 SQLite 파일. 매일 자동 백업(14일), 손상 시 최신 정상 백업으로 복구 제안
- 한국어/영어 UI (Windows 지역 설정에 따라 자동, 설정에서 변경 가능)

## 개발

```powershell
dotnet build SokoNote.sln
dotnet test SokoNote.sln          # 단위·UI·스트레스 테스트
dotnet run --project src/SokoNote -- --data .\testdata   # 별도 데이터 폴더로 실행
```
