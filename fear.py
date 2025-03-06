import requests
import json
import time
from datetime import datetime
import subprocess
import os

# CNN API URL
CNN_FEAR_GREED_URL = "https://production.dataviz.cnn.io/index/fearandgreed/graphdata/"

# Fake browser headers to bypass bot detection
HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36",
    "Accept-Language": "en-US,en;q=0.9",
    "Referer": "https://edition.cnn.com",
    "DNT": "1",
    "Connection": "keep-alive",
}

def get_fear_greed_index():
    """Fetches Fear & Greed Index from CNN and extracts the score"""
    today = datetime.today().strftime('%Y-%m-%d')
    url = CNN_FEAR_GREED_URL + today

    try:
        response = requests.get(url, headers=HEADERS)
        print("Response Status Code:", response.status_code)

        if response.status_code == 200 and response.text.strip():
            data = response.json()
            fear_greed_score = round(data["fear_and_greed"]["score"], 2)
            return today, fear_greed_score
        else:
            print("Error: Empty response or invalid status code.")
            return None, None
    except Exception as e:
        print("Error fetching data:", e)
        return None, None

def update_github(date, index_value):
    """Updates fear_greed_data/fear_greed_data.csv and pushes to GitHub"""
    
    # 1️⃣ Ensure `fear_greed_data/` folder exists
    folder_path = "fear_greed_data"
    if not os.path.exists(folder_path):
        os.makedirs(folder_path)

    # 2️⃣ Save CSV inside `fear_greed_data/`
    filename = os.path.join(folder_path, "fear_greed_data.csv")

    with open(filename, mode="w") as file:
        file.write("date,index\n")
        file.write(f"{date},{index_value}\n")
    
    print(f"✅ Updated {filename} with Fear & Greed Index: {index_value}")

    # 3️⃣ Ensure Git recognizes the file change
    subprocess.run(["git", "add", "-u"], check=True)  # Track file updates

    # 4️⃣ Force Git to always create a commit, even if nothing changed
    try:
        subprocess.run(["git", "commit", "--allow-empty", "-m", f"Updated Fear & Greed Index: {index_value}"], check=True)
    except subprocess.CalledProcessError:
        print("⚠ No new changes to commit, but proceeding with push.")

    # 5️⃣ Push to GitHub with retry logic
    try:
        subprocess.run(["git", "push", "origin", "main"], check=True)
        print("✅ Pushed updated data to GitHub.")
    except subprocess.CalledProcessError as e:
        print("❌ Error pushing to GitHub, retrying...")
        time.sleep(5)  # Wait before retrying
        try:
            subprocess.run(["git", "push", "origin", "main", "--force"], check=True)
            print("✅ Successfully pushed after retry.")
        except subprocess.CalledProcessError as e:
            print("❌ Failed to push after retry:", e)

if __name__ == "__main__":
    date, index_value = get_fear_greed_index()
    if index_value is not None:
        update_github(date, index_value)

    # Run every hour
    time.sleep(3600)
