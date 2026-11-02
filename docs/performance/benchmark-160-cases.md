# 160개 성능 측정 조건

전체 성능 매트릭스는 다음 조합으로 구성한다.

```text
5개 데이터셋 × 2개 프로그램 × 2개 형식 × 2개 작업 × 4개 요청 스레드 = 160개 조건
```

## 조합 항목

- 데이터셋: `large-text`, `incompressible`, `small-files`(1,500개),
  `mixed-project`, `large-file`
- 프로그램: The third-party archiver, MxZip
- 형식: ZIP, 7z
- 작업: 압축 생성, 압축 해제
- 요청 스레드: 1, 4, 8, 16

스레드 값은 각 프로그램에 전달한 요청값이다. 실제 내부 worker 수를 측정한
값으로 해석하지 않는다.

각 조건은 준비 실행 1회와 공식 측정 7회를 수행한다. 전체 명령 실행 수는
1,280회이며, 교차 제품 해제와 SHA-256 파일 트리 검증은 별도 검증 단계다.

## 기록 및 무결성

각 결과에는 wall-time median/p95, CPU 시간, peak RSS, 아카이브 크기, 요청
스레드, 명령, 교차 해제 SHA-256 트리를 기록한다. 압축 결과는 반대 제품으로
해제하여 원본 트리와 비교한다.

## 7z 회귀 판정

회귀 gate는 dataset별 8-thread 7z 압축 결과에서 MxZip과 타사 압축 프로그램이 정확히
한 쌍 존재할 때만 계산한다.

```text
현재 비율 = 현재 MxZip median / 현재 the third-party archiver median
기준 비율 = 기준 MxZip median / 기준 the third-party archiver median
정규화 회귀 = 현재 비율 / 기준 비율 - 1
```

정규화 회귀가 5%를 초과하면 실패한다. 두 제품에 공통으로 발생한 시스템
slowdown은 비율에서 상쇄한다. 절대 중앙값은 정보용으로 계속 기록한다.
쌍의 누락·중복 또는 0·음수·비유한 중앙값은 게시 전에 실패한다.

완료된 raw 160-case 결과는 `--evaluate-existing`으로 matrix를 다시 실행하지
않고 재평가할 수 있다. 새 raw/summary에는 원본 raw 경로와 SHA-256 provenance가
기록되며, 입력 `results` 배열과 측정값은 변경하지 않는다.
