#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ============================================================================
// ChaosMeter (Ensemble) — NQ/MNQ 10:00-10:10 ET 앙상블 카오스 미터
//
// 정의 (RESEARCH_LOG §6.65):
//   10:00:00-10:10:00 ET 창, 10초봉, 격자를 0~9초로 이동시킨 10개 미터의 평균.
//   각 미터 = 중심선((창 최고+최저)/2) 종가 교차 횟수.
//   창 규약 = (시작, 종료] 우측 닫힘, 종료시각 라벨 (§6.64 버그 방지).
//
// 신호등 (동결 규칙): ≤4 초록(정방향) / 4<x<7 노랑(sim 탐색) / ≥7 빨강(역방향)
//
// 핵심 설계 (지난 시행착오 반영):
//   1) 시각은 벽시계 아닌 '데이터 틱 시각' 사용 → playback/replay에서도 정확
//   2) 1초 데이터를 내부 버퍼에 쌓아 10초봉 10정렬을 직접 계산 (차트 주기 무관)
//   3) 10:10:00 도달 시 값 확정 → 패널 표시 + CSV 로그 자동 추출
//   4) 하루 1회만 로그 기록 (중복 방지)
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class ChaosMeter : Indicator
	{
		// ── 1초 데이터 버퍼 (당일 09:29~10:10만 유지)
		private class Tick { public DateTime T; public double Close; public double High; public double Low; }
		private List<Tick> buffer = new List<Tick>();

		private DateTime curDay = DateTime.MinValue;
		private double meterValue = double.NaN;   // 미터창 앙상블 값
		private double auxValue   = double.NaN;   // 9:30-9:45 보조 (계산만, 표시 off)
		private bool   finalizedToday = false;
		private bool   loggedToday = false;
		private string statusText = "대기";

		// 이미 로그에 기록된 날짜 (파일에서 로드) → 재실행/재playback시 중복 방지
		private HashSet<string> loggedDates = new HashSet<string>();
		private bool loggedDatesLoaded = false;

		// 현재시각 표시용 (데이터 기반)
		private DateTime lastTickTime = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "10:00-10:10 앙상블 카오스 미터 (10정렬 평균)";
				Name        = "ChaosMeter";
				Calculate   = Calculate.OnEachTick;
				IsOverlay   = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;

				// 사용자 설정
				ShowAux      = false;                  // 9:30-9:45 보조 표시
				WriteLog     = true;                   // CSV 자동 로그
				LogPath      = @"C:\NQ_Research\chaos_meter_log.csv";
				PanelCorner  = 0;                      // 0=좌상, 1=우상, 2=좌하, 3=우하
				FontSizePx   = 16;
			}
			else if (State == State.Configure)
			{
				// 1초 시리즈 추가 (차트 주기와 무관하게 정확한 계산 위해)
				AddDataSeries(BarsPeriodType.Second, 1);
			}
		}

		protected override void OnBarUpdate()
		{
			// BarsInProgress 1 = 1초 시리즈
			if (BarsInProgress != 1)
				return;

			DateTime t = Times[1][0];      // ★ 데이터 틱 시각 (playback에서도 정확)
			TimeSpan tod = t.TimeOfDay;

			// ── 성능: 계산창(9:29~10:11) 밖에서는 즉시 반환, 아무 작업 안 함 ──
			// 단, 날짜 전환 감지는 가볍게 유지 (하루 첫 틱에서만 리셋)
			if (t.Date != curDay)
			{
				curDay = t.Date;
				buffer.Clear();
				meterValue = double.NaN;
				auxValue = double.NaN;
				finalizedToday = false;
				loggedToday = false;
				statusText = "대기";
			}

			// 계산창 밖이면 여기서 종료 (버퍼링·계산·렌더 트리거 없음)
			if (tod < new TimeSpan(9, 29, 0) || tod > new TimeSpan(10, 11, 0))
				return;

			lastTickTime = t;

			// 09:29~10:11 구간만 버퍼링 (보조창 9:30 + 미터창 10:10 커버)
			buffer.Add(new Tick {
				T = t,
				Close = Closes[1][0],
				High = Highs[1][0],
				Low = Lows[1][0]
			});

			// 진행 상태 표시 (미터창 진입 중)
			if (tod > new TimeSpan(10, 0, 0) && tod <= new TimeSpan(10, 10, 0) && !finalizedToday)
			{
				statusText = string.Format("계산중 {0:mm\\:ss}", tod - new TimeSpan(10, 0, 0));
			}

			// ★ 10:10:00 도달 → 확정
			if (!finalizedToday && tod > new TimeSpan(10, 10, 0))
			{
				meterValue = ComputeEnsemble(new TimeSpan(10, 0, 0), new TimeSpan(10, 10, 0));
				auxValue   = ComputeEnsemble(new TimeSpan(9, 30, 0), new TimeSpan(9, 45, 0));
				finalizedToday = true;
				statusText = "확정";

				if (WriteLog && !loggedToday)
				{
					WriteLogLine();
					loggedToday = true;
				}

				// 확정 후엔 버퍼 비워 메모리 정리 (당일 재계산 불필요)
				buffer.Clear();
			}
		}

		// ── 앙상블 계산: 격자 0~9초 이동한 10개 미터 평균
		private double ComputeEnsemble(TimeSpan start, TimeSpan end)
		{
			List<int> meters = new List<int>();
			for (int off = 0; off < 10; off++)
			{
				int m = ComputeOneMeter(start, end, off);
				if (m >= 0) meters.Add(m);
			}
			if (meters.Count < 10) return double.NaN;   // 데이터 부족
			double sum = 0;
			foreach (int m in meters) sum += m;
			return sum / meters.Count;
		}

		// ── 단일 미터: 특정 격자 오프셋으로 10초봉 만들고 중심선 교차 계산
		//    창 규약 = (start, end] 우측 닫힘, 종료시각 라벨 (§6.64)
		private int ComputeOneMeter(TimeSpan start, TimeSpan end, int offsetSec)
		{
			// 창 내 1초 데이터 추출: t > start & t <= end
			DateTime ds = curDay + start;
			DateTime de = curDay + end;
			List<Tick> win = new List<Tick>();
			foreach (Tick tk in buffer)
				if (tk.T > ds && tk.T <= de)
					win.Add(tk);
			if (win.Count < 30) return -1;

			// 10초봉 구성: 격자 원점 = start + offsetSec
			// 각 봉 = (binStart, binStart+10] 우측 닫힘, 라벨=binEnd
			DateTime origin = ds + TimeSpan.FromSeconds(offsetSec);
			var bars = new SortedDictionary<DateTime, double[]>(); // key=binEnd, val=[high,low,close,lastTicks]

			foreach (Tick tk in win)
			{
				// tk가 속하는 봉의 종료시각 계산 (우측 닫힘, pandas resample과 동일)
				// pandas는 origin보다 앞선 부분구간도 자체 봉으로 만듦 (ceil 그대로, 밀지 않음)
				double secFromOrigin = (tk.T - origin).TotalSeconds;
				long binIdx = (long)Math.Ceiling(secFromOrigin / 10.0);
				DateTime binEnd = origin + TimeSpan.FromSeconds(binIdx * 10);

				if (!bars.ContainsKey(binEnd))
					bars[binEnd] = new double[] { tk.High, tk.Low, tk.Close, tk.T.Ticks };
				else
				{
					double[] b = bars[binEnd];
					if (tk.High > b[0]) b[0] = tk.High;
					if (tk.Low  < b[1]) b[1] = tk.Low;
					// close = 봉 내 마지막 틱 (Ticks 큰 것)
					if (tk.T.Ticks >= b[3]) { b[2] = tk.Close; b[3] = tk.T.Ticks; }
				}
			}

			if (bars.Count < 6) return -1;

			// 중심선 = (창 전체 최고 + 최저)/2
			double hi = double.MinValue, lo = double.MaxValue;
			foreach (var kv in bars)
			{
				if (kv.Value[0] > hi) hi = kv.Value[0];
				if (kv.Value[1] < lo) lo = kv.Value[1];
			}
			double mid = (hi + lo) / 2.0;

			// 종가 중심선 교차 횟수 (0=이전 유지)
			int cnt = 0, prev = 0;
			foreach (var kv in bars)   // SortedDictionary → 시간순
			{
				double c = kv.Value[2];
				int side = c > mid ? 1 : (c < mid ? -1 : prev);
				if (side == 0) continue;
				if (prev != 0 && side != prev) cnt++;
				prev = side;
			}
			return cnt;
		}

		private void WriteLogLine()
		{
			try
			{
				string dir = Path.GetDirectoryName(LogPath);
				if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

				// ── 파일에 이미 있는 날짜 로드 (최초 1회) → 재playback 중복 방지 ──
				if (!loggedDatesLoaded)
				{
					loggedDates.Clear();
					if (File.Exists(LogPath))
					{
						foreach (string line in File.ReadAllLines(LogPath))
						{
							int comma = line.IndexOf(',');
							if (comma > 0)
							{
								string dcol = line.Substring(0, comma).Trim();
								if (dcol.Length == 8 && dcol != "date")   // yyyyMMdd
									loggedDates.Add(dcol);
							}
						}
					}
					loggedDatesLoaded = true;
				}

				string key = curDay.ToString("yyyyMMdd");
				if (loggedDates.Contains(key))
					return;   // 이미 기록된 날 → 재저장 안 함

				bool newFile = !File.Exists(LogPath);
				using (StreamWriter sw = new StreamWriter(LogPath, true, Encoding.UTF8))
				{
					if (newFile)
						sw.WriteLine("date,ens_1000_1010,ens_0930_0945,signal");
					string sig = Signal(meterValue);
					sw.WriteLine(string.Format("{0},{1:0.0},{2:0.0},{3}",
						key, meterValue, auxValue, sig));
				}
				loggedDates.Add(key);   // 세션 내 재저장도 방지
			}
			catch (Exception ex)
			{
				Print("ChaosMeter 로그 실패: " + ex.Message);
			}
		}

		private string Signal(double v)
		{
			if (double.IsNaN(v)) return "NA";
			if (v <= 4) return "GREEN";
			if (v < 7)  return "YELLOW";
			return "RED";
		}

		// ── 화면 패널 렌더링
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (chartControl == null) return;

			var sig = Signal(meterValue);
			SharpDX.Color bg;
			switch (sig)
			{
				case "GREEN":  bg = new SharpDX.Color(30, 160, 70, 220); break;
				case "YELLOW": bg = new SharpDX.Color(200, 170, 30, 220); break;
				case "RED":    bg = new SharpDX.Color(200, 50, 50, 220); break;
				default:       bg = new SharpDX.Color(90, 90, 90, 200); break;
			}

			string line1 = double.IsNaN(meterValue)
				? string.Format("CHAOS: {0}", statusText)
				: string.Format("CHAOS {0:0.0}  [{1}]", meterValue, sig);
			string line2 = ShowAux && !double.IsNaN(auxValue)
				? string.Format("aux(9:30) {0:0.0}", auxValue)
				: "";
			string line3 = lastTickTime == DateTime.MinValue
				? ""
				: string.Format("time {0:HH:mm:ss}", lastTickTime);

			var tf = new SharpDX.DirectWrite.TextFormat(
				NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
				SharpDX.DirectWrite.FontWeight.Bold,
				SharpDX.DirectWrite.FontStyle.Normal, FontSizePx);

			string text = line1;
			if (line2 != "") text += "\n" + line2;
			if (line3 != "") text += "\n" + line3;

			float w = 210, h = (line2 != "" ? 74 : 56);
			float pad = 10;
			float x, y;
			double panelW = chartControl.CanvasRight - chartControl.CanvasLeft;
			double panelH = chartControl.CanvasBottom - chartControl.CanvasTop;
			switch (PanelCorner)
			{
				case 1: x = (float)(chartControl.CanvasRight - w - pad); y = (float)(chartControl.CanvasTop + pad); break;
				case 2: x = (float)(chartControl.CanvasLeft + pad); y = (float)(chartControl.CanvasBottom - h - pad); break;
				case 3: x = (float)(chartControl.CanvasRight - w - pad); y = (float)(chartControl.CanvasBottom - h - pad); break;
				default: x = (float)(chartControl.CanvasLeft + pad); y = (float)(chartControl.CanvasTop + pad); break;
			}

			var rect = new SharpDX.RectangleF(x, y, w, h);
			var brushBg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, bg);
			var brushTx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
			RenderTarget.FillRectangle(rect, brushBg);

			var layoutRect = new SharpDX.RectangleF(x + 8, y + 6, w - 12, h - 8);
			RenderTarget.DrawText(text, tf, layoutRect, brushTx);

			brushBg.Dispose(); brushTx.Dispose(); tf.Dispose();
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "9:30 보조 표시", Order = 1, GroupName = "설정")]
		public bool ShowAux { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "CSV 로그 기록", Order = 2, GroupName = "설정")]
		public bool WriteLog { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "로그 경로", Order = 3, GroupName = "설정")]
		public string LogPath { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "패널 위치(0좌상/1우상/2좌하/3우하)", Order = 4, GroupName = "설정")]
		public int PanelCorner { get; set; }

		[NinjaScriptProperty]
		[Range(8, 40)]
		[Display(Name = "글자 크기", Order = 5, GroupName = "설정")]
		public float FontSizePx { get; set; }
		#endregion
	}
}
