using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Windows.Forms;

namespace KeepThatBallUp
{
    public partial class Form1 : Form
    {
        // Bird
        float birdX = 150;          // stays fixed horizontally, like real Flappy Bird
        float birdY = 250;
        int birdSize = 40;          // draw size on screen (source image is scaled to this)
        float velocityY = 0;
        float gravity = 0.5f;
        float flapStrength = -8f;   // upward kick applied on click/tap

        // Pipes
        class Pipe
        {
            public float X;
            public int GapCenterY;
            public bool Scored;
        }

        List<Pipe> pipes = new List<Pipe>();
        int pipeWidth = 70;
        int pipeGap = 160;          // vertical gap the bird flies through
        float pipeSpeed = 7f;
        float distanceSinceLastPipe = 0f;
        float pipeSpawnDistance = 250f;

        // ---- Difficulty scaling ----
        // Every scoreThreshold points, pipeSpeed steps up by speedIncrement,
        // capped at maxPipeSpeed. This is intentionally simple (score-based
        // tiers) so it's an easy hook point later for a real level system:
        // e.g. Level 1 = tiers 0-2, Level 2 = tiers 3-5, Level 3 = tiers 6-8,
        // then past Level 3 just keep climbing tiers forever for endless mode.
        float basePipeSpeed = 7f;      // doubled from 3.5f
        int scoreThreshold = 5;
        float speedIncrement = 0.8f;   // doubled to keep the same escalation curve
        float maxPipeSpeed = 18f;      // doubled from 9f

        // ---- Bird tilt ----
        float birdRotation = 0f;      // current visual rotation in degrees
        float maxUpTilt = -25f;       // nose-up angle right after a flap
        float maxDownTilt = 90f;      // nose-down angle during a long fall
        float rotationLerpSpeed = 0.15f; // how quickly it eases toward the dive angle

        int score = 0;

        // ---- Level milestone banners ----
        // Background announcement text (e.g. "LEVEL 1 COMPLETE!") shown briefly
        // when score crosses a milestone. Unlike the pause/countdown overlays,
        // this does NOT dim the screen or pause anything — it's drawn behind
        // the pipes/bird and fades in/out on its own while play continues.
        //
        // To add Level 3 later: add another (score, text) entry below, e.g.
        // (30, "LEVEL 2 COMPLETE!\nDIFFICULTY SPIKE - SPEED INCREASED & BOMBS SPAWNING")
        // — everything else (fade timing, drawing, reset-on-restart) already
        // works generically off this list.
        (int Score, string Text)[] levelMilestones = new (int, string)[]
        {
            (12, "LEVEL 1 COMPLETE!\nDIFFICULTY SPIKE - SPEED INCREASED"),
        };
        HashSet<int> shownMilestones = new HashSet<int>();

        string bannerText = null;
        bool bannerActive = false;
        float bannerElapsed = 0f;
        float bannerDuration = 3f;   // total time on screen, including fade
        float bannerFadeIn = 0.4f;
        float bannerFadeOut = 0.7f;

        // ---- Game state ----
        enum GameState { WaitingToStart, Playing, Paused, Resuming, GameOver }
        GameState currentState = GameState.WaitingToStart;

        // Resume countdown ("3, 2, 1, GO!") shown after unpausing with Space.
        int countdownStage = 0;              // 0="3", 1="2", 2="1", 3="GO!"
        float countdownElapsed = 0f;
        float countdownStageDuration = 1f;   // seconds per stage

        Random rand = new Random();
        System.Windows.Forms.Timer gameTimer = new System.Windows.Forms.Timer();

        // ---- Art assets ----
        // Put your files in an "Assets" folder next to the .exe (Assets\bird.png,
        // Assets\pipe_body.png, Assets\pipe_cap.png). If a file is missing this
        // falls back to plain colored shapes so the game still runs.
        Image birdImage;
        Image pipeBodyImage;
        Image pipeCapImage;
        TextureBrush pipeBodyBrush;

        // ---- Sound effects ----
        // Assets\Sounds\flap.wav, score.wav, hit.wav. SoundPlayer only supports
        // .wav files. Missing files are handled gracefully (just stay silent).
        SoundPlayer flapSound;
        SoundPlayer scoreSound;
        SoundPlayer hitSound;

        // Cached fonts — creating a new Font object every OnPaint call (60x/sec)
        // is a real, measurable perf cost. Create once, reuse forever.
        Font scoreFont = new Font("Arial", 20, FontStyle.Bold);
        Font messageFont = new Font("Arial", 20, FontStyle.Bold);
        Font gameOverFont = new Font("Arial", 40, FontStyle.Bold);
        Font countdownFont = new Font("Arial", 60, FontStyle.Bold);
        Font bannerFont = new Font("Arial", 26, FontStyle.Bold | FontStyle.Italic);

        // Windows' default system timer resolution is ~15.6ms, which makes
        // System.Windows.Forms.Timer fire slightly irregularly — this shows up
        // as small stutters even when your code itself runs fast. Requesting
        // 1ms resolution via winmm.dll tightens that up noticeably.
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        static extern uint TimeBeginPeriod(uint uMilliseconds);

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        static extern uint TimeEndPeriod(uint uMilliseconds);

        public Form1()
        {
            InitializeComponent();
            LoadArt();
            LoadSounds();

            TimeBeginPeriod(1);

            // Without this, every Invalidate() causes a visible clear-then-redraw
            // flash. Double buffering draws the next frame off-screen and swaps
            // it in all at once.
            this.DoubleBuffered = true;
            this.SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint,
                true);

            gameTimer.Interval = 16;
            gameTimer.Tick += GameLoop;
            gameTimer.Start();

            // Set sizes based on whatever the window's starting dimensions are,
            // rather than hardcoded pixel numbers.
            RecalculateLayout();

            // Keep a sane minimum so the gap/pipe math never goes negative on a
            // tiny window.
            this.MinimumSize = new Size(400, 300);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalculateLayout();
            Invalidate();
        }

        // Intercepting here (rather than KeyDown) guarantees we get Space/Escape
        // reliably no matter which control has focus, and lets us swallow the
        // keypress so it doesn't also trigger a focused button's click.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space)
            {
                HandleFlapInput();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                HandlePauseInput();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Shared by mouse click and Space bar. Behavior depends on current state:
        // waiting -> start playing; playing -> flap; paused -> begin resume countdown.
        void HandleFlapInput()
        {
            switch (currentState)
            {
                case GameState.WaitingToStart:
                    currentState = GameState.Playing;
                    DoFlap();
                    break;

                case GameState.Playing:
                    DoFlap();
                    break;

                case GameState.Paused:
                    currentState = GameState.Resuming;
                    countdownStage = 0;
                    countdownElapsed = 0f;
                    Invalidate();
                    break;

                    // Resuming / GameOver: ignore input
            }
        }

        void DoFlap()
        {
            velocityY = flapStrength;

            // Snap upward instantly so the flap feels crisp and responsive;
            // the downward tilt during freefall is eased instead (see GameLoop).
            birdRotation = maxUpTilt;

            flapSound?.Play();
        }

        // Esc pauses while playing. Pressing it again during the resume
        // countdown cancels back to paused (nice-to-have, not strictly asked
        // for, but avoids a weird stuck state if you change your mind).
        void HandlePauseInput()
        {
            if (currentState == GameState.Playing)
            {
                currentState = GameState.Paused;
                Invalidate();
            }
            else if (currentState == GameState.Resuming)
            {
                currentState = GameState.Paused;
                Invalidate();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            TimeEndPeriod(1); // release the high-res timer request

            gameTimer?.Stop();
            gameTimer?.Dispose();

            scoreFont?.Dispose();
            messageFont?.Dispose();
            gameOverFont?.Dispose();
            countdownFont?.Dispose();
            bannerFont?.Dispose();
            pipeBodyBrush?.Dispose();
            birdImage?.Dispose();
            pipeBodyImage?.Dispose();
            pipeCapImage?.Dispose();

            flapSound?.Dispose();
            scoreSound?.Dispose();
            hitSound?.Dispose();
        }

        // Recomputes every size/position that should scale with the window,
        // as a fraction of the current ClientSize. Called once at startup and
        // again every time the form is resized.
        void RecalculateLayout()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            // Bird sits a fixed % from the left edge, sized relative to height.
            birdX = w * 0.2f;
            birdSize = (int)(h * 0.07f);
            birdSize = Math.Max(24, Math.Min(birdSize, 70)); // clamp so it never gets silly small/huge

            // Keep the bird on-screen if the window shrank out from under it.
            if (birdY + birdSize > h) birdY = h - birdSize;
            if (birdY < 0) birdY = 0;

            // Pipe dimensions relative to window size.
            pipeWidth = (int)(w * 0.09f);
            pipeWidth = Math.Max(40, Math.Min(pipeWidth, 100));

            pipeGap = (int)(h * 0.28f);
            pipeGap = Math.Max(120, Math.Min(pipeGap, 260));

            pipeSpawnDistance = w * 0.35f;

            // Reposition the restart button to stay centered under "GAME OVER".
            if (restartButton != null)
            {
                restartButton.Left = (w - restartButton.Width) / 2;
                restartButton.Top = h / 2 + 20;
            }
        }

        void LoadArt()
        {
            string assetsDir = Path.Combine(Application.StartupPath, "Assets");

            birdImage = TryLoadImage(Path.Combine(assetsDir, "bird.png"));
            pipeBodyImage = TryLoadImage(Path.Combine(assetsDir, "pipe_body.png"));
            pipeCapImage = TryLoadImage(Path.Combine(assetsDir, "pipe_cap.png"));

            if (pipeBodyImage != null)
            {
                // TextureBrush tiles the image, so a short texture can repeat
                // seamlessly down a pipe of any height.
                pipeBodyBrush = new TextureBrush(pipeBodyImage, System.Drawing.Drawing2D.WrapMode.Tile);
            }
        }

        void LoadSounds()
        {
            string soundsDir = Path.Combine(Application.StartupPath, "Assets", "Sounds");

            flapSound = TryLoadSound(Path.Combine(soundsDir, "flap.wav"));
            scoreSound = TryLoadSound(Path.Combine(soundsDir, "score.wav"));
            hitSound = TryLoadSound(Path.Combine(soundsDir, "hit.wav"));
        }

        Image TryLoadImage(string path)
        {
            try
            {
                if (File.Exists(path))
                    return Image.FromFile(path);
            }
            catch
            {
                // fall through to null -> fallback shape drawing
            }
            return null;
        }

        SoundPlayer TryLoadSound(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var player = new SoundPlayer(path);
                    player.Load(); // pre-buffer so Play() has no first-hit delay
                    return player;
                }
            }
            catch
            {
                // fall through to null -> silently skip playback
            }
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Speed-oriented rendering hints — cheap to set, and this game
            // doesn't need photo-quality anti-aliasing to look good.
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            e.Graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;

            DrawBanner(e.Graphics);
            DrawPipes(e.Graphics);
            DrawBird(e.Graphics);

            // Score
            e.Graphics.DrawString(
                "Score: " + score,
                scoreFont,
                Brushes.Black,
                20,
                20
            );

            if (currentState == GameState.WaitingToStart)
            {
                e.Graphics.DrawString(
                    "Click or press Space to start",
                    messageFont,
                    Brushes.Black,
                    ClientSize.Width / 2 - 160,
                    ClientSize.Height / 2
                );
            }

            if (currentState == GameState.Paused)
            {
                DimBackground(e.Graphics);
                DrawCentered(e.Graphics, "PAUSED", gameOverFont, Brushes.White, -30);
                DrawCentered(e.Graphics, "Press Space to resume", messageFont, Brushes.White, 30);
            }

            if (currentState == GameState.Resuming)
            {
                DimBackground(e.Graphics);
                string[] labels = { "3", "2", "1", "GO!" };
                string label = countdownStage >= 0 && countdownStage < labels.Length ? labels[countdownStage] : "";
                DrawCentered(e.Graphics, label, countdownFont, Brushes.White, 0);
            }

            if (currentState == GameState.GameOver)
            {
                e.Graphics.DrawString(
                    "GAME OVER",
                    gameOverFont,
                    Brushes.Red,
                    ClientSize.Width / 2 - 150,
                    ClientSize.Height / 2 - 50
                );
            }
        }

        // Draws a background milestone announcement (e.g. "LEVEL 1 COMPLETE!")
        // that fades in, holds, then fades out on its own. No dimming, no pause
        // — this sits behind the pipes/bird and gameplay continues normally.
        void DrawBanner(Graphics g)
        {
            if (!bannerActive || bannerText == null)
                return;

            float alpha;
            if (bannerElapsed < bannerFadeIn)
            {
                alpha = bannerElapsed / bannerFadeIn;
            }
            else if (bannerElapsed > bannerDuration - bannerFadeOut)
            {
                alpha = (bannerDuration - bannerElapsed) / bannerFadeOut;
            }
            else
            {
                alpha = 1f;
            }
            alpha = Clamp(alpha, 0f, 1f);

            // Kept fairly translucent even at full strength so it never fully
            // competes with the pipes/bird drawn on top of it.
            int a = (int)(alpha * 160);
            using (var brush = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
            {
                string[] lines = bannerText.Split('\n');
                float totalHeight = 0;
                foreach (var line in lines)
                    totalHeight += g.MeasureString(line, bannerFont).Height;

                float y = (ClientSize.Height - totalHeight) / 2f;
                foreach (var line in lines)
                {
                    SizeF size = g.MeasureString(line, bannerFont);
                    float x = (ClientSize.Width - size.Width) / 2f;
                    g.DrawString(line, bannerFont, brush, x, y);
                    y += size.Height;
                }
            }
        }

        // Semi-transparent overlay used behind the pause/countdown screens so
        // the frozen game underneath is still visible but clearly non-interactive.
        void DimBackground(Graphics g)
        {
            using (var dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            {
                g.FillRectangle(dim, 0, 0, ClientSize.Width, ClientSize.Height);
            }
        }

        // Draws text horizontally centered, vertically offset from the middle
        // of the window by yOffset (negative = above center, positive = below).
        void DrawCentered(Graphics g, string text, Font font, Brush brush, float yOffset)
        {
            SizeF size = g.MeasureString(text, font);
            float x = (ClientSize.Width - size.Width) / 2f;
            float y = (ClientSize.Height - size.Height) / 2f + yOffset;
            g.DrawString(text, font, brush, x, y);
        }

        void DrawBird(Graphics g)
        {
            // Rotate around the bird's own center, draw, then restore the
            // transform so it doesn't affect anything drawn afterward.
            var state = g.Save();

            float centerX = birdX + birdSize / 2f;
            float centerY = birdY + birdSize / 2f;
            g.TranslateTransform(centerX, centerY);
            g.RotateTransform(birdRotation);
            g.TranslateTransform(-centerX, -centerY);

            if (birdImage != null)
            {
                g.DrawImage(birdImage, birdX, birdY, birdSize, birdSize);
            }
            else
            {
                g.FillEllipse(Brushes.Gold, birdX, birdY, birdSize, birdSize);
            }

            g.Restore(state);
        }

        void DrawPipes(Graphics g)
        {
            foreach (var pipe in pipes)
            {
                int topHeight = pipe.GapCenterY - pipeGap / 2;
                int bottomY = pipe.GapCenterY + pipeGap / 2;
                int bottomHeight = ClientSize.Height - bottomY;

                if (pipeBodyBrush != null)
                {
                    // Anchor the texture at this pipe's X so it doesn't smear
                    // sideways as pipes scroll at slightly different offsets.
                    pipeBodyBrush.ResetTransform();
                    pipeBodyBrush.TranslateTransform(pipe.X, 0);

                    g.FillRectangle(pipeBodyBrush, pipe.X, 0, pipeWidth, topHeight);
                    g.FillRectangle(pipeBodyBrush, pipe.X, bottomY, pipeWidth, bottomHeight);
                }
                else
                {
                    g.FillRectangle(Brushes.ForestGreen, pipe.X, 0, pipeWidth, topHeight);
                    g.FillRectangle(Brushes.ForestGreen, pipe.X, bottomY, pipeWidth, bottomHeight);
                }

                if (pipeCapImage != null)
                {
                    // Scale the cap to match the current pipeWidth (which changes
                    // with window size) instead of drawing it at a fixed native
                    // pixel size — otherwise the visual cap can overhang wider
                    // or narrower than the actual pipe hitbox on some window sizes.
                    const float capWidthRatio = 1.17f; // cap is slightly wider than the shaft, like a real pipe lip
                    float aspect = pipeCapImage.Height / (float)pipeCapImage.Width;
                    int capW = (int)(pipeWidth * capWidthRatio);
                    int capH = (int)(capW * aspect);
                    float capX = pipe.X - (capW - pipeWidth) / 2f;

                    // Top pipe's cap sits at the mouth (bottom of the top pipe)
                    g.DrawImage(pipeCapImage, capX, topHeight - capH, capW, capH);
                    // Bottom pipe's cap sits at its mouth (top of the bottom pipe)
                    g.DrawImage(pipeCapImage, capX, bottomY, capW, capH);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            HandleFlapInput();
        }

        void GameLoop(object sender, EventArgs e)
        {
            switch (currentState)
            {
                case GameState.WaitingToStart:
                case GameState.Paused:
                    return; // nothing animates, no need to keep repainting

                case GameState.Resuming:
                    UpdateCountdown();
                    Invalidate();
                    return;

                case GameState.GameOver:
                    return; // timer is stopped in this state anyway

                case GameState.Playing:
                    break; // fall through to normal physics below
            }

            // Gravity
            velocityY += gravity;
            birdY += velocityY;

            // Advance the milestone banner fade, if one is showing. This runs
            // alongside normal gameplay — nothing pauses for it.
            if (bannerActive)
            {
                bannerElapsed += gameTimer.Interval / 1000f;
                if (bannerElapsed >= bannerDuration)
                {
                    bannerActive = false;
                    bannerText = null;
                }
            }

            // Bird tilt: eases toward an angle proportional to fall speed, so
            // it noses down more the faster it's falling, capped at maxDownTilt.
            // (The upward snap on flap happens instantly in OnMouseDown.)
            float targetRotation = Clamp(velocityY * 4f, maxUpTilt, maxDownTilt);
            birdRotation = Lerp(birdRotation, targetRotation, rotationLerpSpeed);

            // Ceiling / ground collision
            if (birdY <= 0)
            {
                birdY = 0;
                velocityY = 0;
            }
            if (birdY + birdSize >= ClientSize.Height)
            {
                birdY = ClientSize.Height - birdSize;
                EndGame();
            }

            // Move pipes
            for (int i = pipes.Count - 1; i >= 0; i--)
            {
                pipes[i].X -= pipeSpeed;

                // Score when the bird clears a pipe
                if (!pipes[i].Scored && pipes[i].X + pipeWidth < birdX)
                {
                    pipes[i].Scored = true;
                    score++;
                    scoreSound?.Play();
                    UpdateDifficulty();
                    CheckMilestones();
                }

                // Remove off-screen pipes
                if (pipes[i].X + pipeWidth < 0)
                {
                    pipes.RemoveAt(i);
                    continue;
                }

                // Collision check
                if (CheckPipeCollision(pipes[i]))
                {
                    EndGame();
                }
            }

            // Spawn new pipes at a steady horizontal distance
            distanceSinceLastPipe += pipeSpeed;
            if (distanceSinceLastPipe >= pipeSpawnDistance)
            {
                distanceSinceLastPipe = 0f;
                SpawnPipe();
            }

            Invalidate();
        }

        bool CheckPipeCollision(Pipe pipe)
        {
            // Shrink the collision box inward from the drawn sprite size. The
            // placeholder bird image has transparent padding around the visible
            // bird, so checking the full birdSize square was registering hits
            // in empty space the player couldn't see — this makes the hitbox
            // match what's actually visible (and feels more forgiving besides).
            float inset = birdSize * 0.18f;
            float hitLeft = birdX + inset;
            float hitRight = birdX + birdSize - inset;
            float hitTop = birdY + inset;
            float hitBottom = birdY + birdSize - inset;

            // Only check pipes that overlap the bird's X range
            if (hitRight < pipe.X || hitLeft > pipe.X + pipeWidth)
                return false;

            int topHeight = pipe.GapCenterY - pipeGap / 2;
            int bottomY = pipe.GapCenterY + pipeGap / 2;

            bool hitsTop = hitTop < topHeight;
            bool hitsBottom = hitBottom > bottomY;

            return hitsTop || hitsBottom;
        }

        void SpawnPipe()
        {
            int margin = 80; // keep gap away from very top/bottom
            int gapCenterY = rand.Next(margin + pipeGap / 2, ClientSize.Height - margin - pipeGap / 2);

            pipes.Add(new Pipe
            {
                X = ClientSize.Width,
                GapCenterY = gapCenterY,
                Scored = false
            });
        }

        // Steps pipeSpeed up every scoreThreshold points, capped at maxPipeSpeed.
        // Note that pipe spawn frequency naturally increases along with this too,
        // since distanceSinceLastPipe accumulates by pipeSpeed each frame.
        void UpdateDifficulty()
        {
            int tier = score / scoreThreshold;
            pipeSpeed = Math.Min(maxPipeSpeed, basePipeSpeed + tier * speedIncrement);
        }

        // Fires a background banner exactly once per milestone score, using
        // shownMilestones to guard against re-triggering on the same run.
        void CheckMilestones()
        {
            foreach (var milestone in levelMilestones)
            {
                if (score == milestone.Score && !shownMilestones.Contains(milestone.Score))
                {
                    shownMilestones.Add(milestone.Score);
                    bannerText = milestone.Text;
                    bannerActive = true;
                    bannerElapsed = 0f;
                }
            }
        }

        float Lerp(float a, float b, float t) => a + (b - a) * t;
        float Clamp(float v, float min, float max) => Math.Max(min, Math.Min(max, v));

        // Advances the "3, 2, 1, GO!" sequence by one tick's worth of real time.
        // Stages: 0="3", 1="2", 2="1", 3="GO!" — once stage 3 finishes, resume play.
        void UpdateCountdown()
        {
            countdownElapsed += gameTimer.Interval / 1000f;
            if (countdownElapsed >= countdownStageDuration)
            {
                countdownElapsed = 0f;
                countdownStage++;

                if (countdownStage > 3)
                {
                    currentState = GameState.Playing;
                }
            }
        }

        void EndGame()
        {
            currentState = GameState.GameOver;
            gameTimer.Stop();
            restartButton.Visible = true;
            hitSound?.Play();
        }

        private void restartButton_Click(object sender, EventArgs e)
        {
            birdY = 250;
            velocityY = 0;
            birdRotation = 0f;
            score = 0;
            pipeSpeed = basePipeSpeed;

            pipes.Clear();
            distanceSinceLastPipe = 0f;

            currentState = GameState.WaitingToStart;
            countdownStage = 0;
            countdownElapsed = 0f;

            bannerActive = false;
            bannerText = null;
            bannerElapsed = 0f;
            shownMilestones.Clear();

            restartButton.Visible = false;

            gameTimer.Start();

            Invalidate();
        }
    }
}