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
    now = datetime.now()
    timestamp = now.strftime('%Y-%m-%d %H:%M:%S')  # Fetch date & time

    url = CNN_FEAR_GREED_URL + now.strftime('%Y-%m-%d')

    try:
        response = requests.get(url, headers=HEADERS)
        print("Response Status Code:", response.status_code)

        if response.status_code == 200 and response.text.strip():
            data = response.json()
            fear_greed_score = round(data["fear_and_greed"]["score"], 2)
            return timestamp, fear_greed_score
        else:
            print("Error: Empty response or invalid status code.")
            return None, None
    except Exception as e:
        print("Error fetching data:", e)
        return None, None

def update_github(timestamp, index_value):
    """Updates fear_greed_data/fg_index.csv and pushes to GitHub"""
    
    # 1️⃣ Ensure `fear_greed_data/` folder exists
    folder_path = "fear_greed_data"
    if not os.path.exists(folder_path):
        os.makedirs(folder_path)

    # 2️⃣ Save CSV inside `fear_greed_data/`
    filename = os.path.join(folder_path, "fg_index.csv")

    # 3️⃣ Append new data if file exists, otherwise create a new file
    file_exists = os.path.isfile(filename)
    
    with open(filename, mode="a" if file_exists else "w") as file:
        if not file_exists:
            file.write("timestamp,index\n")  # Write header if new file
        file.write(f"{timestamp},{index_value}\n")
    
    print(f"✅ Updated {filename} with Fear & Greed Index: {index_value} at {timestamp}")

    # 4️⃣ Ensure Git recognizes the file change
    subprocess.run(["git", "add", "-u"], check=True)  

    # 5️⃣ Force Git to always create a commit, even if nothing changed
    try:
        subprocess.run(["git", "commit", "--allow-empty", "-m", f"Updated Fear & Greed Index: {index_value} at {timestamp}"], check=True)
    except subprocess.CalledProcessError:
        print("⚠ No new changes to commit, but proceeding with push.")

    # 6️⃣ Push to GitHub with retry logic
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
    while True:
        timestamp, index_value = get_fear_greed_index()
        if index_value is not None:
            update_github(timestamp, index_value)

        print("⏳ Waiting for the next update in 1 hour...")
        time.sleep(3600)  # Fetch data every 1 hour
